-- BeamSplit single-instance backend.
-- RenderViews are texture outputs, so each player's named texture is composited
-- into the launcher-provided rectangle with ImGui.
local M = {}
M.dependencies = {'core_camera'}

local logTag = 'BeamSplitSplitScreen'
local sessionPath = '/settings/beamsplit/session.json'
local im = ui_imgui
local imUtils = require('ui/imguiUtils')
local playerTags = require('ge/extensions/render/playerTags')

local session
local active, pendingStart = false, false
local views = {}
local lastW, lastH = 0, 0
local previousMultiseat, previousAutoAssign
local uiHidden, spawnRequested = false, false
local reseatTimer = 0
local compositeErrorLogged = false
local pickerModels
local pickers = {}
local runtimeActionBackup = {}

local function capabilityError()
  if not RenderViewManagerInstance then return 'RenderViewManagerInstance is unavailable' end
  if not core_camera or not core_camera.createContext or not core_camera.setContextCamera then
    return 'camera contexts are unavailable'
  end
  if not core_input_bindings or not core_input_bindings.setPlayerToDevice then
    return 'per-device player assignment is unavailable'
  end
  if not im or not imUtils then return 'ImGui compositor is unavailable' end
  return nil
end

local function readSession()
  local value = jsonReadFile(sessionPath)
  if type(value) ~= 'table' or value.schemaVersion ~= 1 or type(value.players) ~= 'table' then
    return nil
  end
  return value
end

local function drivableVehicles()
  local result = {}
  for _, veh in activeVehiclesIterator() do
    if not veh.isTraffic and not veh.isParked then result[#result + 1] = veh end
  end
  return result
end

local function patchRuntimeActions()
  local actions = core_input_actions and core_input_actions.getActiveActions()
  if not actions then return false end
  local replacements = {
    toggleMenues = "extensions.render_splitScreen.nav(PLAYER,'menu')",
    menu_item_up = "extensions.render_splitScreen.nav(PLAYER,'up')",
    menu_item_down = "extensions.render_splitScreen.nav(PLAYER,'down')",
    menu_item_select = "extensions.render_splitScreen.nav(PLAYER,'ok')",
    menu_item_back = "extensions.render_splitScreen.nav(PLAYER,'back')",
    switch_next_vehicle_multiseat = "extensions.render_splitScreen.nextCar(PLAYER,1)",
    switch_next_vehicle = "extensions.render_splitScreen.nextCar(PLAYER,1)",
    switch_previous_vehicle = "extensions.render_splitScreen.nextCar(PLAYER,-1)"
  }
  for name, command in pairs(replacements) do
    local action = actions[name]
    if action then
      runtimeActionBackup[name] = {
        onDown = action.onDown, onUp = action.onUp, onChange = action.onChange,
        source = action.source
      }
      action.onDown, action.onUp, action.onChange = command, nil, nil
      log('I', logTag, 'runtime action patched: ' .. name .. ' (was ' .. tostring(action.source) .. ')')
    else
      log('E', logTag, 'runtime action missing: ' .. name)
    end
  end
  -- Do not rebuild bindings synchronously while setPlayerToDevice is running;
  -- BeamNG 0.39 can crash in the native assignPlayerToDevice path. Queue the
  -- ordinary input-map refresh after all device reassignment has completed.
  if core_input_bindings and core_input_bindings.onFileChanged then
    core_input_bindings.onFileChanged('/settings/inputmaps/beamsplit-runtime.diff', 'modified')
  end
  return true
end

local function restoreRuntimeActions()
  local actions = core_input_actions and core_input_actions.getActiveActions()
  if actions then
    for name, saved in pairs(runtimeActionBackup) do
      local action = actions[name]
      if action then
        action.onDown, action.onUp, action.onChange = saved.onDown, saved.onUp, saved.onChange
      end
    end
  end
  runtimeActionBackup = {}
  if core_input_bindings and core_input_bindings.onFileChanged then
    core_input_bindings.onFileChanged('/settings/inputmaps/beamsplit-runtime.diff', 'modified')
  end
end

local function configureDevices()
  previousMultiseat = settings.getValue('multiseat')
  previousAutoAssign = core_input_bindings.autoAssignPlayersToDevices
  settings.setValue('multiseat', true)
  core_input_bindings.autoAssignPlayersToDevices = false
  for _, p in ipairs(session.players) do
    if p.device then core_input_bindings.setPlayerToDevice(p.device, p.player) end
    if p.keyboard then
      core_input_bindings.setPlayerToDevice('keyboard0', p.player)
      core_input_bindings.setPlayerToDevice('mouse0', p.player)
    end
  end
  local actionMap = scenetree.findObject('MultiseatActionMap')
  if actionMap then actionMap:setEnabled(true) end
  patchRuntimeActions()
end

local function reseat()
  if not active then return end
  local claimed, needy = {}, {}
  for _, v in ipairs(views) do
    local veh = getPlayerVehicle(v.player)
    local id = veh and veh:getID()
    if id and not claimed[id] then claimed[id] = true else needy[#needy + 1] = v end
  end
  for _, veh in ipairs(drivableVehicles()) do
    if #needy == 0 then break end
    if not claimed[veh:getID()] then
      local v = table.remove(needy, 1)
      be:enterVehicle(v.player, veh)
      claimed[veh:getID()] = true
      log('I', logTag, 'seated player ' .. v.player .. ' in vehicle ' .. veh:getID())
    end
  end
end

local function spawnMissingCars()
  local vehicles = drivableVehicles()
  local missing = #views - #vehicles
  if missing <= 0 then reseat(); return true end
  local base = getPlayerVehicle(0) or vehicles[1]
  if not base then return false end
  local origin, rotation = base:getPosition(), base:getRotation()
  local right = base:getDirectionVectorUp():cross(base:getDirectionVector()):normalized()
  local model = base:getJBeamFilename()
  for i = 1, missing do
    core_vehicles.spawnNewVehicle(model, {
      config = base.partConfig,
      pos = origin + right * (4 * (#vehicles + i - 1)),
      rot = rotation,
      autoEnterVehicle = false
    })
  end
  spawnRequested = true
  log('I', logTag, 'requested ' .. missing .. ' additional vehicle(s)')
  return true
end

local function ensureView(v)
  if v.rv then return v.rv end
  local rv = RenderViewManagerInstance:getOrCreateView(v.name)
  if not rv then return nil end
  rv.luaOwned = true
  rv.renderCubemap = false
  rv.renderEditorIcons = false
  rv.namedTexTargetColor = v.name
  v.rv = rv
  return rv
end

local function applyLayout(w, h)
  for _, v in ipairs(views) do
    local r = v.normalized
    v.rect = {
      x = math.floor(r[1] * w), y = math.floor(r[2] * h),
      w = math.max(64, math.floor(r[3] * w)), h = math.max(64, math.floor(r[4] * h))
    }
    local rv = ensureView(v)
    if rv and (v.resW ~= v.rect.w or v.resH ~= v.rect.h) then
      rv.resolution = Point2I(v.rect.w, v.rect.h)
      rv.viewPort = RectI(0, 0, v.rect.w, v.rect.h)
      v.resW, v.resH = v.rect.w, v.rect.h
    end
  end
  lastW, lastH = w, h
end

local function rebuildTags()
  local tags = {}
  for i, v in ipairs(views) do
    tags[i] = {player = v.player, label = 'Player ' .. i,
      color = playerTags.color(i, #views), hideInViews = {v.name}}
  end
  playerTags.rebuild(tags)
end

-- Per-player quick menu ------------------------------------------------------
-- BeamNG's CEF pause/vehicle menus exist only once and are owned by player 0.
-- While split-screen is active, the controller menu button opens this small
-- compositor-native picker in that player's tile instead.
local function loadPickerModels()
  if pickerModels then return pickerModels end
  local list = core_vehicles.getModelList()
  local source = type(list) == 'table' and (list.models or list) or {}
  pickerModels = {}
  for key, data in pairs(source) do
    if type(key) == 'string' then
      local info = type(data) == 'table' and data or {}
      local kind = info.Type
      if kind == nil or kind == 'Car' or kind == 'Truck' then
        pickerModels[#pickerModels + 1] = {key = key, name = tostring(info.Name or key)}
      end
    end
  end
  table.sort(pickerModels, function(a, b) return a.name:lower() < b.name:lower() end)
  log('I', logTag, 'quick menu loaded ' .. #pickerModels .. ' vehicle models')
  return pickerModels
end

local function pickerToggle(player)
  if pickers[player] then pickers[player] = nil; return false end
  if #loadPickerModels() == 0 then return false end
  pickers[player] = {index = 1}
  return true
end

local function pickerStep(player, direction)
  local state = pickers[player]
  if not state then return false end
  local count = #loadPickerModels()
  state.index = ((state.index - 1 + direction) % count) + 1
  return true
end

local function pickerConfirm(player)
  local state = pickers[player]
  if not state then return false end
  local choice = loadPickerModels()[state.index]
  pickers[player] = nil
  local veh = getPlayerVehicle(player)
  if not choice or not veh then return false end
  core_vehicles.replaceVehicle(choice.key, {}, veh)
  simTimeAuthority.pause(false)
  log('I', logTag, 'player ' .. player .. ' selected ' .. choice.key)
  return true
end

local function drawPicker(v, ordinal)
  local state = pickers[v.player]
  if not state then return end
  local models = loadPickerModels()
  local rows, rowH, width = 5, 24, math.min(330, v.rect.w - 40)
  local height = (rows * 2 + 1) * rowH + 58
  local x = v.rect.x + (v.rect.w - width) * 0.5
  local y = v.rect.y + (v.rect.h - height) * 0.5
  local dl = im.GetWindowDrawList()
  local bg = im.GetColorU322(im.ImVec4(0.015, 0.02, 0.03, 0.94))
  local white = im.GetColorU322(im.ImVec4(1, 1, 1, 0.92))
  local muted = im.GetColorU322(im.ImVec4(1, 1, 1, 0.58))
  local c = playerTags.color(ordinal, #views)
  local accent = im.GetColorU322(im.ImVec4(c[1], c[2], c[3], 1))
  local selected = im.GetColorU322(im.ImVec4(c[1], c[2], c[3], 0.3))
  im.ImDrawList_AddRectFilled(dl, im.ImVec2(x, y), im.ImVec2(x + width, y + height), bg, 7, 0)
  im.ImDrawList_AddRectFilled(dl, im.ImVec2(x, y), im.ImVec2(x + width, y + 4), accent, 7, 0)
  im.ImDrawList_AddText1(dl, im.ImVec2(x + 14, y + 13), white,
    'P' .. ordinal .. ' VEHICLE  -  up/down, A select, B close', nil)
  for offset = -rows, rows do
    local index = ((state.index - 1 + offset) % #models) + 1
    local rowY = y + 45 + (offset + rows) * rowH
    if offset == 0 then
      im.ImDrawList_AddRectFilled(dl, im.ImVec2(x + 8, rowY - 2),
        im.ImVec2(x + width - 8, rowY + rowH - 3), selected, 3, 0)
    end
    im.ImDrawList_AddText1(dl, im.ImVec2(x + 18, rowY), offset == 0 and white or muted,
      models[index].name, nil)
  end
end

local function nextFreeCar(player, direction)
  local occupied, candidates = {}, {}
  for _, v in ipairs(views) do
    if v.player ~= player then
      local veh = getPlayerVehicle(v.player)
      if veh then occupied[veh:getID()] = true end
    end
  end
  local current = getPlayerVehicle(player)
  local currentId = current and current:getID()
  for _, veh in ipairs(drivableVehicles()) do
    if not occupied[veh:getID()] then candidates[#candidates + 1] = veh end
  end
  if #candidates < 2 then return false end
  local index = 1
  for i, veh in ipairs(candidates) do if veh:getID() == currentId then index = i; break end end
  local target = candidates[((index - 1 + direction) % #candidates) + 1]
  if target then be:enterVehicle(player, target); return true end
  return false
end

local function compositeFlags()
  return bit.bor(im.WindowFlags_NoDecoration, im.WindowFlags_NoInputs,
    im.WindowFlags_NoNav, im.WindowFlags_NoSavedSettings,
    im.WindowFlags_NoBringToFrontOnFocus, im.WindowFlags_NoFocusOnAppearing,
    im.WindowFlags_NoDocking)
end

local function drawHud(v, ordinal)
  if session.hud == false then return end
  local veh = getPlayerVehicle(v.player)
  local speed = veh and math.abs(veh:getVelocity():length()) * 3.6 or 0
  local dl = im.GetWindowDrawList()
  local bg = im.GetColorU322(im.ImVec4(0.02, 0.02, 0.02, 0.72))
  local accent = playerTags.color(ordinal, #views)
  local ink = im.GetColorU322(im.ImVec4(accent[1], accent[2], accent[3], 1))
  local x, y = v.rect.x + 18, v.rect.y + v.rect.h - 55
  im.ImDrawList_AddRectFilled(dl, im.ImVec2(x, y), im.ImVec2(x + 180, y + 38), bg, 5, 0)
  im.ImDrawList_AddText1(dl, im.ImVec2(x + 10, y + 10), ink,
    string.format('P%d   %.0f km/h', ordinal, speed), nil)
end

local function composite()
  if not Engine.imgui.isEnabled() then ui_visibility.setImgui(true) end
  im.SetNextWindowPos(im.ImVec2(0, 0))
  im.SetNextWindowSize(im.ImVec2(lastW, lastH))
  im.PushStyleVar2(im.StyleVar_WindowPadding, im.ImVec2(0, 0))
  im.PushStyleVar1(im.StyleVar_WindowBorderSize, 0)
  im.PushStyleVar1(im.StyleVar_WindowRounding, 0)
  if im.Begin('##beamsplitComposite', nil, compositeFlags()) then
    local ok, err = pcall(function()
      for i, v in ipairs(views) do
        local tex = imUtils.texObj('#' .. v.name)
        if tex and tex.texId then
          im.SetCursorPos(im.ImVec2(v.rect.x, v.rect.y))
          im.Image(tex.texId, im.ImVec2(v.rect.w, v.rect.h))
        end
        drawHud(v, i)
        drawPicker(v, i)
      end
    end)
    if not ok and not compositeErrorLogged then
      compositeErrorLogged = true
      log('E', logTag, 'compositor failed: ' .. tostring(err))
    end
  end
  im.End()
  im.PopStyleVar(3)
end

local function start()
  if active then return true end
  local err = capabilityError()
  if err then
    log('E', logTag, err)
    guihooks.trigger('toastrMsg', {type='error', title='BeamSplit unavailable', msg=err})
    return false
  end
  session = readSession()
  if not session or #session.players ~= 2 then
    log('E', logTag, 'invalid two-player session manifest')
    return false
  end
  views = {}
  pickers, pickerModels = {}, nil
  for i, p in ipairs(session.players) do
    local name = 'beamsplitView' .. tostring(i - 1)
    local v = {player = p.player, name = name, normalized = p.rect}
    views[#views + 1] = v
    ensureView(v)
    core_camera.createContext(name, name, p.player)
  end
  active, pendingStart = true, false
  configureDevices()
  local io = im.GetIO()
  applyLayout(io.DisplaySize.x, io.DisplaySize.y)
  for _, v in ipairs(views) do core_camera.setContextCamera(v.name, session.camera or 'orbit') end
  rebuildTags()
  if not uiHidden then ui_visibility.setCef(false); uiHidden = true end
  ui_visibility.setImgui(true)
  spawnMissingCars()
  guihooks.trigger('splitScreenPlayers', #views)
  log('I', logTag, 'single-instance split-screen active')
  return true
end

local function stop()
  if not active and not session then return end
  active = false
  guihooks.trigger('splitScreenPlayers', 0)
  playerTags.reset()
  for _, v in ipairs(views) do
    core_camera.destroyContext(v.name)
    if v.rv and RenderViewManagerInstance then RenderViewManagerInstance:destroyView(v.rv) end
  end
  views = {}
  pickers, pickerModels = {}, nil
  restoreRuntimeActions()
  if core_input_bindings then core_input_bindings.autoAssignPlayersToDevices = previousAutoAssign ~= false end
  if previousMultiseat ~= nil then settings.setValue('multiseat', previousMultiseat) end
  local actionMap = scenetree.findObject('MultiseatActionMap')
  if actionMap and not previousMultiseat then actionMap:setEnabled(false) end
  if uiHidden then ui_visibility.setCef(true); uiHidden = false end
  session, pendingStart, spawnRequested = nil, false, false
  lastW, lastH, reseatTimer = 0, 0, 0
  log('I', logTag, 'split-screen stopped')
end

function M.onClientPostStartMission()
  session = readSession()
  pendingStart, spawnRequested = session ~= nil, false
end

function M.onUpdate(dtReal)
  if pendingStart and getPlayerVehicle(0) then start() end
  if not active then return end
  local io = im.GetIO()
  if io.DisplaySize.x ~= lastW or io.DisplaySize.y ~= lastH then
    applyLayout(io.DisplaySize.x, io.DisplaySize.y)
  end
  composite()
  playerTags.updatePositions()
  reseatTimer = reseatTimer + (dtReal or 0)
  if reseatTimer >= 1 then
    reseatTimer = 0
    if not spawnRequested and #drivableVehicles() < #views then spawnMissingCars() else reseat() end
  end
end

function M.onVehicleSpawned()
  if active then reseat(); simTimeAuthority.pause(false) end
end
function M.onVehicleSwitched(_, _, player)
  if active and player ~= nil and views[player + 1] then
    core_camera.setContextCamera(views[player + 1].name, session.camera or 'orbit')
  end
end
function M.nav(player, action)
  if not active then return false end
  player = tonumber(player) or 0
  -- Opening the stock pause screen can toggle the simulation before the merged
  -- binding reaches us. A local quick menu must never pause the other player.
  simTimeAuthority.popPauseRequest('ui_pause_home_tab')
  simTimeAuthority.pause(false)
  if action == 'menu' then
    local opened = pickerToggle(player)
    log('I', logTag, 'player ' .. player .. ' quick menu ' .. (opened and 'opened' or 'closed'))
    return opened
  end
  if action == 'back' then pickers[player] = nil; return true end
  if action == 'up' then return pickerStep(player, -1) end
  if action == 'down' then return pickerStep(player, 1) end
  if action == 'ok' then return pickerConfirm(player) end
  return false
end
function M.nextCar(player, direction)
  if not active then return false end
  player = tonumber(player) or 0
  direction = direction == -1 and -1 or 1
  if pickers[player] then return pickerStep(player, direction) end
  return nextFreeCar(player, direction)
end
function M.resetVehicle(player)
  local veh = getPlayerVehicle(player)
  if veh then veh:requestReset() end
end
function M.switchCar(player, id)
  local veh = getObjectByID(id)
  if veh then be:enterVehicle(player, veh) end
end
function M.toggleFreeCam(player)
  if not active or not views[(player or 0) + 1] then return false end
  local v = views[player + 1]
  local state = core_camera.getContextCameraState(v.name)
  core_camera.setContextCamera(v.name, state and state.cam == 'free' and 'orbit' or 'free')
  return true
end

M.start = start
M.stop = stop
M.isActive = function() return active end
M.onExtensionUnloaded = stop
M.onSerialize = function() stop(); playerTags.reset() end

return M
