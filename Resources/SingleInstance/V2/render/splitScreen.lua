-- BeamSplit v2 -- local split-screen multiplayer inside a single BeamNG instance.
--
-- How it fits together (all three pieces already exist in 0.39, they were just never wired up):
--   * input   -- the engine's multiseat support maps each gamepad to a player index and seats
--                that player in a vehicle (settings.multiseat, core_multiseat, be:enterVehicle).
--   * camera  -- core_camera "contexts": one camera pipeline per player, each pointed at its own
--                RenderView, each reading its own MoveManager (core_camera.createContext).
--   * output  -- one RenderView per player rendering to a named texture, composited into tiles
--                over the window by the imgui overlay below.
--
-- The engine already references this extension by name (core/multiseat.lua defers the player
-- tags to us, ge/server/commands.lua routes the free-cam toggle through us), so dropping it in
-- at /lua/ge/extensions/render/splitScreen.lua makes those integrations light up on their own.

local M = {}
M.dependencies = {'core_camera'}

local im = ui_imgui
local imUtils = require('ui/imguiUtils')
local layout = require('ge/extensions/render/splitScreenLayout')
local playerTags = require('ge/extensions/render/playerTags')
local hud = require('ge/extensions/render/splitScreenHud')
local picker = require('ge/extensions/render/splitScreenPicker')
local menu = require('ge/extensions/render/splitScreenMenu')

local MAX_PLAYERS = 4
local VIEW_PREFIX = 'splitScreenP' -- RenderView name == camera context id == named texture

local cfg = {
  count = 2,
  mode = 'auto',      -- see splitScreenLayout.modes
  renderScale = 1.0,  -- render each tile at this fraction of its on-screen size
  gutter = 2,         -- px of black between tiles
  hideUi = true,      -- the HUD belongs to player 0 only, so hide it while split
  showTags = true,    -- floating player name tags
  showHud = true,     -- per-tile gauge cluster
  autoSeat = true,    -- move players who are sharing a car into a free one (see reseat)
  audio = 'shared',   -- 'shared' | 'native' | player index -- where the single SFX listener sits
  rects = nil,        -- optional launcher-provided normalized {x,y,w,h} rectangles
  deviceAssignments = nil, -- optional launcher-provided {player, device, keyboard} records
  debug = false,      -- log the compositor's inputs once a second (see debugComposite)
}

local active = false
local views = {}      -- 1-based, views[i].player == i-1
local lastW, lastH = 0, 0
local uiHidden = false
local taggedCount = nil
local compositeErrors = 0
local suspended = false
local pendingSpawns = 0 -- cars requested but not yet drivable; see spawnCarsForPlayers
local lifecycle = nil   -- queued renderer-unsafe operation; only the affected view is fenced when possible
local seatGuard = nil   -- briefly re-check exclusivity after multiseat settles a replacement
local queueLifecycleOperation

local STOCK_SPLIT_FILTER = 'beamsplitStockActions'
local STOCK_PICKER_FILTER = 'beamsplitPickerActions'

local function setActionFilter(group, actions, blocked)
  if not core_input_actionFilter then return end
  core_input_actionFilter.setGroup(group, actions)
  core_input_actionFilter.addAction(0, group, blocked)
end

-- Start/Back are also BeamNG's pause/map buttons. While the split owns them, allowing the stock
-- actions through opens hidden CEF screens underneath the compositor and can leave big-map camera
-- state active. The picker likewise owns D-pad/A/B while it is open.
local function blockStockSplitActions(blocked)
  setActionFilter(STOCK_SPLIT_FILTER,
    {'toggleMenues', 'pause', 'toggleBigMap', 'toggle_minimap'}, blocked)
end

local function pickerIsOpen()
  for _, v in ipairs(views) do if picker.isOpen(v.player) then return true end end
  return false
end

local function updatePickerActionFilter()
  setActionFilter(STOCK_PICKER_FILTER, {
    'accept', 'bigMapControllerSelect', 'menu_item_select', 'shiftUp', 'triggerAction0',
    'decline', 'menu_item_back', 'parkingbrake', 'switch_next_vehicle_multiseat',
    'toggleRadialMenuMulti', 'reset_physics', 'recover_vehicle', 'look_back',
    'menu_item_left', 'menu_item_right', 'menu_item_up', 'menu_item_down'
  }, active and (pickerIsOpen() or menu.isOpen()))
end

local function viewName(player) return VIEW_PREFIX .. player end

-- RenderView + camera context for one player ---------------------------------

local function ensureRenderView(v)
  if v.rv then return v.rv end
  local rv = RenderViewManagerInstance and RenderViewManagerInstance:getOrCreateView(v.name)
  if not rv then return nil end
  rv.luaOwned = true              -- let the GC tear the view down with us
  rv.renderCubemap = false
  rv.renderEditorIcons = false
  rv.namedTexTargetColor = v.name -- what imUtils.texObj('#'..name) resolves to
  v.rv = rv
  return rv
end

-- The camera mode filter (cameraModes/gameengine.lua) derives the frustum from rv.resolution
-- every frame, so resolution is the only thing we have to keep in sync with the tile size.
local function applyResolution(v)
  local rv = ensureRenderView(v)
  if not rv or not v.rect then return end
  local w = math.max(64, math.floor(v.rect.w * cfg.renderScale))
  local h = math.max(64, math.floor(v.rect.h * cfg.renderScale))
  if v.resW == w and v.resH == h then return end
  rv.resolution = Point2I(w, h)
  rv.viewPort = RectI(0, 0, w, h)
  v.resW, v.resH = w, h
end

-- Vehicle changes only need the camera pipeline detached.  Keep the RenderView itself alive:
-- recreating it makes BeamNG allocate another swap-chain-backed window target, which can escape
-- onto a second monitor and also causes the visible split -> main camera -> split flash.
local function destroyContext(v)
  if v.contextAlive then
    core_camera.destroyContext(v.id)
    v.contextAlive = false
  end
end

local function destroyView(v)
  destroyContext(v)
  if v.rv and RenderViewManagerInstance then
    RenderViewManagerInstance:destroyView(v.rv)
  end
  v.rv, v.resW, v.resH = nil, nil, nil
end

local function createView(v)
  ensureRenderView(v)
  if not v.contextAlive then
    core_camera.createContext(v.id, v.name, v.player)
    v.contextAlive = true
  end
end

-- Player tags ----------------------------------------------------------------
-- Each tag is blacklisted from its own player's view, so you see everyone's tag but your own.

local function rebuildTags()
  if not cfg.showTags then
    if taggedCount then playerTags.destroy(); taggedCount = nil end
    return
  end
  local tags = {}
  for i, v in ipairs(views) do
    tags[i] = {
      player = v.player,
      label = core_locales.contextTranslate('ui.multiseat.playerTag', {number = v.player + 1}),
      color = playerTags.color(i, #views),
      hideInViews = {v.name},
    }
  end
  playerTags.rebuild(tags)
  taggedCount = #views
end

-- Audio ----------------------------------------------------------------------
-- There is exactly one SFX listener and no API to place it: SFXSystem exposes getPositionXYZ /
-- getForwardXYZ but no setter, and the listener simply follows the main camera. The main camera
-- is still live behind the tiles (it renders the fullscreen view the overlay covers) and it
-- tracks player 0 -- which is why, untouched, all sound comes from the first tile's car.
--
-- So we drive the main camera ourselves. 'shared' parks it at the centroid of the players' cameras
-- so everyone is audible; a player index parks it on that player for correct first-person audio.
-- This cannot give each tile its own mix -- one listener is a hard engine limit.

local function listenerTarget()
  local mode = cfg.audio
  -- Native is the least surprising mix: do not fight core_camera at all, so BeamNG owns its
  -- listener exactly as in single-player. Player 2 remains a spatial world source when nearby.
  if mode == 'native' or mode == 'off' then return nil end

  if type(mode) == 'number' then
    local v = views[mode + 1]
    local veh = v and getPlayerVehicle(v.player)
    if veh then
      local st = core_camera.getContextCameraState(v.id)
      -- Follow the actual rendered camera, not the vehicle origin under the chassis. The latter
      -- made direction, occlusion and cockpit/exterior transitions noticeably wrong.
      return (st and st.pos) or veh:getPosition(), st and st.rot
    end
    return nil
  end

  -- "center" was the name used by early BeamSplit builds; keep accepting it so an old
  -- session manifest or console command upgrades without falling back to player 1.
  if mode ~= 'shared' and mode ~= 'center' then return nil end

  local sum, count, rot = vec3(0, 0, 0), 0, nil
  for _, v in ipairs(views) do
    local veh = getPlayerVehicle(v.player)
    if veh then
      local st = core_camera.getContextCameraState(v.id)
      local pos = st and st.pos
      sum = sum + (pos and vec3(pos.x, pos.y, pos.z) or veh:getPosition())
      count = count + 1
      if not rot then
        rot = st and st.rot
      end
    end
  end
  if count == 0 then return nil end
  return sum / count, rot
end

local function updateListener()
  local pos, rot = listenerTarget()
  if not pos then return end
  rot = rot or {x = 0, y = 0, z = 0, w = 1}
  -- same C entry point core_camera's own output filter uses for the main view; called from our
  -- onUpdate so it lands after core_camera has run and wins for the frame
  setCameraPosRotFovNearClipC(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, 65, 0.1)
end

-- Device assignment ----------------------------------------------------------
-- Stock multiseat derives the player count from the number of *controllers* and always gives the
-- keyboard player 0, so two pads become players 1 and 2 -- player 2 falling outside a 2-way split
-- entirely, which reads as "both pads drive the same car". We want pad N -> player N, so
-- BeamSplit takes ownership of the mapping instead. autoAssignPlayersToDevices gates every
-- automatic reassignment in core_input_bindings, so turning it off stops multiseat overwriting us.

local savedAutoAssign = nil

local function padNames()
  local pads = {}
  for devname in pairs(core_input_bindings.devices or {}) do
    if not (devname:startswith('keyboard') or devname:startswith('mouse')) then
      pads[#pads + 1] = devname
    end
  end
  table.sort(pads) -- stable order, so pad -> player does not shuffle between runs
  return pads
end

local function assignDevices()
  if savedAutoAssign == nil then savedAutoAssign = core_input_bindings.autoAssignPlayersToDevices end
  core_input_bindings.autoAssignPlayersToDevices = false

  local assignments = cfg.deviceAssignments
  if type(assignments) == 'table' and #assignments > 0 then
    for _, assignment in ipairs(assignments) do
      local player = math.max(0, math.min(#views - 1, tonumber(assignment.player) or 0))
      if assignment.device then
        local devname = tostring(assignment.device)
        -- assignPlayerToDevice is a native function and crashes BeamNG when passed a controller
        -- that disappeared during the loading screen. Never call through unless the input layer
        -- has actually enumerated that exact device for this mission.
        if (core_input_bindings.devices or {})[devname] then
          core_input_bindings.setPlayerToDevice(devname, player)
        else
          log('E', 'beamsplit', 'controller ' .. devname .. ' is unavailable; skipping player ' .. player .. ' assignment')
          guihooks.trigger('toastrMsg', {
            type = 'warning', title = 'Controller unavailable',
            msg = devname .. ' disconnected while loading. Reconnect it, then restart the split.'
          })
        end
      end
      if assignment.keyboard then
        for devname in pairs(core_input_bindings.devices or {}) do
          if devname:startswith('keyboard') or devname:startswith('mouse') then
            core_input_bindings.setPlayerToDevice(devname, player)
          end
        end
      end
    end
  else
    -- Standalone behavior: keyboard/mouse remain a spare input for player 0 and pads are stable.
    for devname in pairs(core_input_bindings.devices or {}) do
      if devname:startswith('keyboard') or devname:startswith('mouse') then
        core_input_bindings.setPlayerToDevice(devname, 0)
      end
    end
    local pads = padNames()
    for i, devname in ipairs(pads) do
      -- surplus pads double up on the last tile rather than driving an off-screen player
      core_input_bindings.setPlayerToDevice(devname, math.min(i - 1, #views - 1))
    end
  end

  -- only multiseat toggles this, and only from the auto-assign path we just disabled; without it
  -- the per-player "cycle vehicle" binding is dead
  local am = scenetree.findObject('MultiseatActionMap')
  if am then am:setEnabled(true) end

  log('I', 'beamsplit', type(assignments) == 'table' and #assignments > 0
    and ('applied ' .. #assignments .. ' launcher device assignment(s)')
    or ('assigned ' .. #padNames() .. ' pad(s) in stable device order'))
end

local function restoreDevices()
  if savedAutoAssign ~= nil then
    core_input_bindings.autoAssignPlayersToDevices = savedAutoAssign
    savedAutoAssign = nil
  end
  local am = scenetree.findObject('MultiseatActionMap')
  if am then am:setEnabled(false) end
end

-- Seating --------------------------------------------------------------------
-- Stock multiseat puts every player in the *least occupied* vehicle, so with one car on the map
-- all players pile into it as co-drivers. These give each player a car of their own.

local function drivableVehicles()
  local res = {}
  for _, veh in activeVehiclesIterator() do
    if not veh.isTraffic and not veh.isParked then res[#res + 1] = veh end
  end
  return res
end

-- Assign every view a unique car. The preferred player/car pair is claimed first, which matters
-- after a picker replacement: multiseat can momentarily put another player into the new object
-- while the old seat is being destroyed. Anyone without another free car is put on foot instead
-- of being left able to steer somebody else's vehicle.
local function enforceExclusiveSeats(preferredPlayer, preferredVehicleId, desiredSeats)
  if not cfg.autoSeat or not active then return false end
  local claimed, assignments = {}, {}
  local cars = drivableVehicles()

  local function available(id)
    return id and id >= 0 and getObjectByID(id) ~= nil and not claimed[id]
  end

  local order = {}
  if preferredPlayer ~= nil then order[#order + 1] = preferredPlayer end
  for _, v in ipairs(views) do
    if v.player ~= preferredPlayer then order[#order + 1] = v.player end
  end

  for _, player in ipairs(order) do
    local current = getPlayerVehicle(player)
    local currentId = current and current:getID()
    local wanted = player == preferredPlayer and preferredVehicleId
      or (desiredSeats and desiredSeats[player])
    local chosen
    if available(wanted) then chosen = wanted
    elseif available(currentId) then chosen = currentId
    else
      for _, car in ipairs(cars) do
        if available(car:getID()) then chosen = car:getID(); break end
      end
    end
    assignments[player] = chosen or false
    if chosen then claimed[chosen] = true end
  end

  local changed = false
  -- Write the preferred seat last so any stock auto-assignment callback cannot steal it.
  for _, v in ipairs(views) do
    local player = v.player
    if player ~= preferredPlayer then
      local targetId = assignments[player]
      local current = getPlayerVehicle(player)
      local currentId = current and current:getID()
      if targetId and currentId ~= targetId then
        be:enterVehicle(player, getObjectByID(targetId)); changed = true
      elseif not targetId and currentId then
        be:exitVehicle(player); changed = true
      end
    end
  end
  if preferredPlayer ~= nil then
    local targetId = assignments[preferredPlayer]
    local current = getPlayerVehicle(preferredPlayer)
    local currentId = current and current:getID()
    if targetId and currentId ~= targetId then
      be:enterVehicle(preferredPlayer, getObjectByID(targetId)); changed = true
    elseif not targetId and currentId then
      be:exitVehicle(preferredPlayer); changed = true
    end
  end
  return changed
end

-- Deliberately conservative: only players *sharing* a car get moved, and only into a car nobody
-- has claimed. A player who picked their own car (D-pad up cycles vehicles) is left alone.
local function reseat()
  if not cfg.autoSeat or not active then return end
  local claimed, needy = {}, {}
  for _, v in ipairs(views) do
    local veh = getPlayerVehicle(v.player)
    local vid = veh and veh:getID()
    if vid and not claimed[vid] then
      claimed[vid] = true
    else
      needy[#needy + 1] = v
    end
  end
  if #needy == 0 then return end
  for _, veh in ipairs(drivableVehicles()) do
    if #needy == 0 then break end
    if not claimed[veh:getID()] then
      local v = table.remove(needy, 1)
      be:enterVehicle(v.player, veh)
      claimed[veh:getID()] = true
      log('I', 'beamsplit', 'seated player ' .. v.player .. ' in vehicle ' .. veh:getID())
    end
  end
  return #needy -- players still without a car of their own
end

-- Cycle one player through the cars nobody else is driving. Skipping occupied cars is what keeps
-- this from fighting reseat(), which exists to pull players *out* of shared vehicles -- cycling
-- into someone else's seat would just get undone a second later.
local function nextCar(player, dir)
  if not active then return false end
  dir = (dir == -1) and -1 or 1
  -- while that player's picker is open the same stick/d-pad scrolls the model list instead, so
  -- one pair of bindings covers both "swap to another car here" and "choose a different model"
  if picker.isOpen(player) then return picker.move(player, dir) end

  local occupied = {}
  for _, v in ipairs(views) do
    if v.player ~= player then
      local veh = getPlayerVehicle(v.player)
      if veh then occupied[veh:getID()] = true end
    end
  end

  local mine = getPlayerVehicle(player)
  local myId = mine and mine:getID()
  local candidates = {}
  for _, veh in ipairs(drivableVehicles()) do
    if not occupied[veh:getID()] then candidates[#candidates + 1] = veh end
  end
  if #candidates < 2 then
    log('I', 'beamsplit', 'player ' .. player .. ': no other free car to swap to')
    return false
  end

  local idx = 1
  for i, veh in ipairs(candidates) do
    if veh:getID() == myId then idx = i; break end
  end
  local target = candidates[((idx - 1 + dir) % #candidates) + 1]
  if target and target:getID() ~= myId then
    be:enterVehicle(player, target)
    log('I', 'beamsplit', 'player ' .. player .. ' swapped to vehicle ' .. target:getID())
  end
  return true
end

-- Spawn copies until there is one car per player, then seat everyone. `model` defaults to
-- whatever player 0 is driving.
local function spawnCarsForPlayers(model)
  if not active then return end
  local vehs = drivableVehicles()
  -- A spawned car takes seconds to become drivable and only then shows up in drivableVehicles().
  -- Without counting the ones already in flight, a second call in that window (toggling the split
  -- off and on, or pressing the bind twice) sees the old count and spawns another round -- which
  -- is how two players ended up with three cars.
  local missing = #views - #vehs - pendingSpawns
  if missing <= 0 then return reseat() end
  local base = getPlayerVehicle(0) or vehs[1]
  if not base then log('E', 'beamsplit', 'no vehicle to copy -- spawn one first'); return end
  model = model or base:getJBeamFilename()
  local origin, rot = base:getPosition(), quat(0, 0, 0, 1)
  local right = base:getDirectionVectorUp():cross(base:getDirectionVector()):normalized()
  -- Spawning mutates scene/render resources. BeamNG 0.39 can access-violate if our extra
  -- RenderViews are traversing those resources at the same time, so the actual calls run only
  -- after the custom contexts have been down long enough for queued render work to flush.
  return queueLifecycleOperation('spawn vehicles', function()
    for i = 1, missing do
      -- Count before calling: BeamNG may fire onVehicleSpawned synchronously, before
      -- spawnNewVehicle returns.
      pendingSpawns = pendingSpawns + 1
      local ok, spawned = pcall(core_vehicles.spawnNewVehicle, model,
        {pos = origin + right * (4 * (#vehs + i - 1)), rot = rot})
      if not ok or spawned == false then
        pendingSpawns = math.max(0, pendingSpawns - 1)
        log('E', 'beamsplit', 'vehicle spawn failed: ' .. tostring(spawned))
      end
    end
  end, true, {hard = true})
end

-- Layout ---------------------------------------------------------------------

local function configuredRects(w, h)
  if type(cfg.rects) == 'table' and #cfg.rects == #views then
    local result = {}
    for i, source in ipairs(cfg.rects) do
      local x = tonumber(source.x or source[1])
      local y = tonumber(source.y or source[2])
      local rw = tonumber(source.w or source[3])
      local rh = tonumber(source.h or source[4])
      if not x or not y or not rw or not rh then return layout.compute(#views, w, h, cfg.mode, cfg.gutter) end
      result[i] = {
        x = math.floor(x * w), y = math.floor(y * h),
        w = math.max(1, math.floor(rw * w)), h = math.max(1, math.floor(rh * h))
      }
    end
    return result
  end
  return layout.compute(#views, w, h, cfg.mode, cfg.gutter)
end

local function relayout(w, h)
  local rects = configuredRects(w, h)
  for i, v in ipairs(views) do
    v.rect = rects[i]
    applyResolution(v)
  end
  lastW, lastH = w, h
end

-- Renderer lifecycle ---------------------------------------------------------
--
-- Vehicle spawn/replacement and screenshot capture all rebuild render resources. Keeping a
-- camera context attached to the vehicle being rebuilt reproducibly crashes BeamNG 0.39 in native
-- code. Isolate the affected context (or all contexts for ownerless work), let queued rendering
-- drain, perform the operation, then reconnect the same session-long RenderViews. The views must
-- not be recreated here:
-- BeamNG backs them with window targets and repeated allocation can produce an extra monitor view.

local function resumeViews()
  if not active then lifecycle = nil; return end
  local job = lifecycle
  local reason = job and job.reason or 'operation'
  if job and job.hard and not job.targeted then
    settings.setValue('multiseat', true)
    assignDevices()
    for player, oldId in pairs(job.seats or {}) do
      -- The picker target is assigned last, below. Restoring its old seat here lets multiseat's
      -- reseat pass claim the freshly spawned car for player 1 before player 2 can enter it.
      if player ~= job.assignPlayer then
        local id = oldId
        local veh = id and getObjectByID(id)
        if veh then be:enterVehicle(player, veh) end
      end
    end
  end
  if job and job.targeted then
    local target = views[job.assignPlayer + 1]
    if target then createView(target) end
  else
    for _, v in ipairs(views) do createView(v) end
  end
  local vp = im.GetMainViewport()
  relayout(vp.Size.x, vp.Size.y)
  if job and job.targeted then
    local target = views[job.assignPlayer + 1]
    if target then core_camera.setContextCamera(target.id, 'orbit') end
  else
    for _, v in ipairs(views) do core_camera.setContextCamera(v.id, 'orbit') end
  end
  rebuildTags()
  -- Claim the picker result first and restore everyone else to a different vehicle. This is more
  -- deterministic than relying on multiseat's least-occupied heuristic during object replacement.
  if job and job.hard and job.assignPlayer ~= nil then
    local id = job.targetVehicleId or (job.switched and job.switched[job.assignPlayer])
    local veh = id and getObjectByID(id)
    if veh then
      enforceExclusiveSeats(job.assignPlayer, id, job.seats)
      log('I', 'beamsplit', 'assigned player ' .. job.assignPlayer .. ' to vehicle ' .. id)
    else
      log('E', 'beamsplit', 'could not assign player ' .. job.assignPlayer ..
        ' to spawned vehicle ' .. tostring(id))
    end
  else reseat() end
  if job and job.hard and job.assignPlayer ~= nil then
    seatGuard = {
      remaining = 3.0,
      nextCheck = 0,
      player = job.assignPlayer,
      vehicleId = job.targetVehicleId or (job.switched and job.switched[job.assignPlayer]),
      seats = job.seats,
    }
  end
  lifecycle = nil
  ui_visibility.setImgui(true)
  log('I', 'beamsplit', reason .. ' settled -- split views restored')
end

queueLifecycleOperation = function(reason, operation, waitForVehicles, opts)
  if not active then return false end
  if lifecycle then
    log('W', 'beamsplit', 'cannot start ' .. reason .. ' while ' .. lifecycle.reason .. ' is settling')
    return false
  end
  opts = opts or {}
  lifecycle = {
    reason = reason,
    operation = operation,
    waitForVehicles = waitForVehicles == true,
    preRemaining = opts.preDelay or 0.5,
    postDelay = opts.postDelay or 1.5,
    postRemaining = nil,
    watchdog = opts.watchdog or 45,
    hard = opts.hard == true,
    seats = {},
    switched = {},
    assignPlayer = opts.assignPlayer,
    -- A player-owned vehicle change can isolate only that player's camera and seat. The other
    -- camera contexts keep driving their session-long RenderViews, so their tiles and controls
    -- remain live while BeamNG builds the new vehicle. Operations without an owner (initial
    -- fill, screenshots) retain the conservative full-session fence.
    targeted = opts.hard == true and opts.assignPlayer ~= nil
      and views[(opts.assignPlayer or 0) + 1] ~= nil,
    paused = false,
    ran = false,
  }
  log('I', 'beamsplit', reason .. ' queued')
  return true
end

local function updateLifecycle(dt)
  local job = lifecycle
  if not job then return false end
  dt = dt or 0

  -- Always tear down at the start of our update. A picker/menu action can queue work after the
  -- compositor has already submitted this frame; destroying its textures there would race the
  -- very render work this guard exists to protect against.
  if not job.paused then
    if job.hard then
      for _, v in ipairs(views) do
        local veh = getPlayerVehicle(v.player)
        if veh then job.seats[v.player] = veh:getID() end
      end
    end
    -- Leave the named-texture RenderViews allocated for the whole BeamSplit session. Replacing a
    -- vehicle invalidates camera state only for the player following that vehicle, so fence that
    -- context alone. Keeping unrelated contexts alive is what lets the other players continue.
    if job.targeted then
      destroyContext(views[job.assignPlayer + 1])
    else
      for _, v in ipairs(views) do destroyContext(v) end
    end
    playerTags.destroy(); taggedCount = nil
    if job.hard then
      if job.targeted then
        -- Detach only the seat whose object may be replaced. Other players keep their multiseat
        -- input routes and vehicles; importantly, no global settings/action-map churn occurs.
        be:exitVehicle(job.assignPlayer)
      else
        settings.setValue('multiseat', false)
        restoreDevices()
        for p = 1, #views - 1 do be:exitVehicle(p) end
      end
    end
    job.paused = true
    log('I', 'beamsplit', job.targeted
      and (job.reason .. ' -- isolated player ' .. (job.assignPlayer + 1) .. ' view; others remain live')
      or (job.reason .. ' -- split views paused'))
    return true
  end

  if not job.ran then
    job.preRemaining = job.preRemaining - dt
    if job.preRemaining > 0 then return true end
    job.ran = true
    local ok, err = pcall(job.operation)
    if not ok then log('E', 'beamsplit', job.reason .. ' failed: ' .. tostring(err)) end
    if not job.waitForVehicles or pendingSpawns <= 0 then job.postRemaining = job.postDelay end
    return true
  end

  if job.waitForVehicles and pendingSpawns > 0 then
    job.watchdog = job.watchdog - dt
    if job.watchdog > 0 then return true end
    log('E', 'beamsplit', job.reason .. ' timed out waiting for vehicle load; restoring views')
    pendingSpawns = 0
    job.postRemaining = job.postDelay
  end

  if job.postRemaining == nil then job.postRemaining = job.postDelay end
  job.postRemaining = job.postRemaining - dt
  if job.postRemaining <= 0 then resumeViews() end
  return true
end

local function replacePlayerVehicle(model, config, veh, player)
  player = player or 0
  local vehicleOptions = config and {config = config} or {}
  local shared = veh == nil
  if veh then
    for _, v in ipairs(views) do
      if v.player ~= player then
        local other = getPlayerVehicle(v.player)
        if other and other:getID() == veh:getID() then shared = true; break end
      end
    end
  end

  if shared then
    local base = veh or getPlayerVehicle(0) or drivableVehicles()[1]
    if not base then
      log('E', 'beamsplit', 'player ' .. (player + 1) .. ': no existing car to spawn beside')
      return false
    end
    local pos = base:getPosition()
    local right = base:getDirectionVectorUp():cross(base:getDirectionVector()):normalized()
    return queueLifecycleOperation('spawn player ' .. (player + 1) .. ' vehicle', function()
      pendingSpawns = pendingSpawns + 1
      local options = deepcopy(vehicleOptions)
      options.pos = pos + right * (4 * (player + 1))
      options.rot = quat(0, 0, 0, 1)
      local ok, spawned = pcall(core_vehicles.spawnNewVehicle, model, options)
      if not ok or spawned == false then
        pendingSpawns = math.max(0, pendingSpawns - 1)
        error(ok and 'spawnNewVehicle returned false' or spawned)
      end
      local id = spawned and spawned.getID and spawned:getID()
      if lifecycle and id then
        lifecycle.targetVehicleId = id
        lifecycle.switched[player] = id
      end
    end, true, {hard = true, assignPlayer = player})
  end

  return queueLifecycleOperation('replace player ' .. (player + 1) .. ' vehicle', function()
    pendingSpawns = pendingSpawns + 1
    local ok, result = pcall(core_vehicles.replaceVehicle, model, vehicleOptions, veh)
    if not ok or result == false then
      pendingSpawns = math.max(0, pendingSpawns - 1)
      error(ok and 'replaceVehicle returned false' or result)
    end
    local id = result and result.getID and result:getID()
    if lifecycle and id then
      lifecycle.targetVehicleId = id
      lifecycle.switched[player] = id
    end
  end, true, {hard = true, assignPlayer = player})
end

local function resetPlayerVehicle(player, pressed)
  player = player or 0
  if not active then return false end
  if pressed ~= false and (lifecycle or picker.isOpen(player) or menu.isOpen()) then return false end
  local veh = getPlayerVehicle(player)
  if not veh then return false end
  if pressed == false then
    veh:queueLuaCommand('recovery.stopRecovering(' .. player .. ')')
  else
    -- This is BeamNG's stock D-pad-left recovery path, targeted at the correct player's car.
    -- Keeping its down/up pair preserves rewind and smart-recovery behavior instead of reducing
    -- the binding to an in-place physics reset.
    veh:queueLuaCommand([[obj:queueGameEngineLua('extensions.hook("trackVehReset")') recovery.startRecovering()]])
    log('I', 'beamsplit', 'recovering player ' .. player .. ' vehicle')
  end
  return true
end

local function spawnVehicleSafely(model, opts)
  return queueLifecycleOperation('spawn vehicle', function()
    pendingSpawns = pendingSpawns + 1
    local ok, spawned = pcall(core_vehicles.spawnNewVehicle, model, opts or {})
    if not ok or spawned == false then
      pendingSpawns = math.max(0, pendingSpawns - 1)
      error(ok and 'spawnNewVehicle returned false' or spawned)
    end
  end, true, {hard = true})
end

local function captureScreenshot()
  return queueLifecycleOperation('screenshot', function()
    screenshot.takeScreenShot()
  end, false, {preDelay = 0.5, postDelay = 1.0})
end

-- Compositor -----------------------------------------------------------------
-- One borderless, input-transparent, full-window imgui overlay; each tile is the player's
-- RenderView texture blitted at its rect.

local compositeFlagsCache
local function compositeFlags()
  if not compositeFlagsCache then
    compositeFlagsCache = bit.bor(
      im.WindowFlags_NoDecoration,
      im.WindowFlags_NoInputs,
      im.WindowFlags_NoNav,
      im.WindowFlags_NoSavedSettings,
      im.WindowFlags_NoBringToFrontOnFocus,
      im.WindowFlags_NoFocusOnAppearing,
      im.WindowFlags_NoDocking)
  end
  return compositeFlagsCache
end

local function mainViewport()
  local vp = im.GetMainViewport()
  return vp, vp.Pos.x, vp.Pos.y, vp.Size.x, vp.Size.y
end

local function screenRect(r)
  local _, ox, oy = mainViewport()
  return {x = r.x + ox, y = r.y + oy, w = r.w, h = r.h}
end

-- Cover the stock main camera while the custom camera contexts are safely detached. The backing
-- RenderViews remain allocated, but we deliberately do not sample their textures during vehicle
-- destruction; this is a lightweight, intentional transition inside the one BeamNG window.
local function lifecycleComposite()
  if not Engine.imgui.isEnabled() then ui_visibility.setImgui(true) end

  local vp, ox, oy, w, h = mainViewport()
  if w ~= lastW or h ~= lastH then
    lastW, lastH = w, h
    local rects = configuredRects(w, h)
    for i, v in ipairs(views) do v.rect = rects[i] end
  end

  im.SetNextWindowViewport(vp.ID)
  im.SetNextWindowPos(vp.Pos)
  im.SetNextWindowSize(im.ImVec2(w, h))
  im.PushStyleVar2(im.StyleVar_WindowPadding, im.ImVec2(0, 0))
  im.PushStyleVar1(im.StyleVar_WindowBorderSize, 0)
  im.PushStyleVar1(im.StyleVar_WindowRounding, 0)
  im.PushStyleColor2(im.Col_WindowBg, im.ImVec4(0.025, 0.032, 0.045, 1))

  local function body()
    local dl = im.GetWindowDrawList()
    local panel = im.GetColorU322(im.ImVec4(0.045, 0.06, 0.08, 1))
    local hair = im.GetColorU322(im.ImVec4(1, 1, 1, 0.10))
    for _, v in ipairs(views) do
      local r = v.rect
      local sr = {x = r.x + ox, y = r.y + oy, w = r.w, h = r.h}
      local changing = lifecycle and lifecycle.assignPlayer == v.player
      local canFreeze = lifecycle and lifecycle.assignPlayer ~= nil and not changing
      local tex = canFreeze and imUtils.texObj('#' .. v.name) or nil
      if tex and tex.texId then
        -- The unaffected player's camera context is paused too because vehicle resource rebuilds
        -- are global, but its session-long render target safely retains the last completed frame.
        im.SetCursorPos(im.ImVec2(r.x, r.y))
        im.Image(tex.texId, im.ImVec2(r.w, r.h))
      else
        im.ImDrawList_AddRectFilled(dl, im.ImVec2(sr.x, sr.y),
          im.ImVec2(sr.x + sr.w, sr.y + sr.h), panel, 0, 0)
      end
      im.ImDrawList_AddRect(dl, im.ImVec2(sr.x, sr.y),
        im.ImVec2(sr.x + sr.w, sr.y + sr.h), hair, 0, 0, 1)

      if not canFreeze then
        local title = 'PLAYER ' .. (v.player + 1)
        local status = changing and 'CHANGING VEHICLE...' or 'PLEASE WAIT...'
        local titleSize, statusSize = 18, 12
        im.SetWindowFontScale(titleSize / math.max(1, im.GetFontSize()))
        im.SetCursorPos(im.ImVec2(r.x + (r.w - #title * titleSize * 0.54) * 0.5,
          r.y + r.h * 0.5 - 30))
        im.TextColored(im.ImVec4(0.36, 0.70, 1, 1), title)
        im.SetWindowFontScale(statusSize / math.max(1, im.GetFontSize()))
        im.SetCursorPos(im.ImVec2(r.x + (r.w - #status * statusSize * 0.54) * 0.5,
          r.y + r.h * 0.5 + 4))
        im.TextColored(im.ImVec4(1, 1, 1, 0.62), status)
        im.SetWindowFontScale(1)
      end
    end
  end

  if im.Begin('##beamsplitVehicleChange', nil, compositeFlags()) then
    local ok, err = pcall(body)
    if not ok then
      log('E', 'beamsplit', 'vehicle-change overlay draw failed: ' .. tostring(err))
    end
  end
  im.End()
  im.PopStyleColor(1)
  im.PopStyleVar(3)
end

-- one-line-per-second dump of everything the compositor depends on, for when tiles do not appear
local debugTimer = 0
local function debugComposite(dt, drawn)
  debugTimer = debugTimer + dt
  if debugTimer < 1 then return end
  debugTimer = 0
  local parts = {string.format('imguiEnabled=%s display=%dx%d drawn=%d/%d',
    tostring(Engine.imgui.isEnabled()), lastW, lastH, drawn, #views)}
  for _, v in ipairs(views) do
    local tex = imUtils.texObj('#' .. v.name)
    local size = tex and tex.size
    parts[#parts + 1] = string.format('%s: rect=%d,%d %dx%d res=%sx%s texId=%s texSize=%sx%s',
      v.name, v.rect.x, v.rect.y, v.rect.w, v.rect.h, tostring(v.resW), tostring(v.resH),
      tostring(tex and tex.texId), tostring(size and size.x), tostring(size and size.y))
  end
  log('I', 'beamsplit.debug', table.concat(parts, '  |  '))
end

local function composite()
  -- While the split is active the imgui layer *is* the picture: the tiles only reach the screen
  -- through it. Anything that turns imgui off -- a screenshot (renderViews' capture job calls
  -- ui_visibility.set(false)), photo mode, or a player pressing the stock "toggle UI" bind --
  -- leaves the window showing the bare main camera instead. Re-assert it rather than hoping
  -- nothing else touches it.
  if not Engine.imgui.isEnabled() then ui_visibility.setImgui(true) end

  local vp = im.GetMainViewport()
  im.SetNextWindowViewport(vp.ID)
  im.SetNextWindowPos(vp.Pos)
  im.SetNextWindowSize(vp.Size)
  im.PushStyleVar2(im.StyleVar_WindowPadding, im.ImVec2(0, 0))
  im.PushStyleVar1(im.StyleVar_WindowBorderSize, 0)
  im.PushStyleVar1(im.StyleVar_WindowRounding, 0)
  local drawn = 0

  local function body()
    for _, v in ipairs(views) do
      local tex = imUtils.texObj('#' .. v.name)
      if tex and tex.texId then
        im.SetCursorPos(im.ImVec2(v.rect.x, v.rect.y))
        im.Image(tex.texId, im.ImVec2(v.rect.w, v.rect.h))
        drawn = drawn + 1
      end
    end
    -- same draw list as the tiles above, so the clusters land on top of their own tile
    if cfg.showHud then
      local dl = im.GetWindowDrawList()
      for i, v in ipairs(views) do
        local veh = getPlayerVehicle(v.player)
        hud.subscribe(veh) -- idempotent; cheap enough to re-assert every frame
        hud.draw(dl, screenRect(v.rect), v.player, playerTags.color(i, #views), veh)
      end
    end
    local dl = im.GetWindowDrawList()
    for i, v in ipairs(views) do
      picker.draw(dl, screenRect(v.rect), v.player, playerTags.color(i, #views))
    end
  end

  if im.Begin('##beamsplitComposite', nil, compositeFlags()) then
    -- Begin/End and the style stack must balance every frame no matter what: an error thrown in
    -- here would otherwise skip End() and leave imgui stuck on "Missing End()", killing the whole
    -- overlay rather than just the offending widget.
    local ok, err = pcall(body)
    if not ok then
      compositeErrors = compositeErrors + 1
      if compositeErrors == 1 then
        log('E', 'beamsplit', 'compositor draw failed (further errors suppressed): ' .. tostring(err))
      end
    end
  end
  im.End()
  im.PopStyleVar(3)
  return drawn
end

-- Public API -----------------------------------------------------------------

local function isActive() return active end

-- Step out of the split without tearing it down.
--
-- The stock menus -- vehicle selector, parts, options -- are CEF screens, and the split hides CEF
-- (`hideUi`) *and* covers the window with an opaque overlay, so they are both invisible and
-- unreachable while it is running. Suspending stops compositing and puts CEF back, leaving the
-- render views and camera contexts alive, so the game behaves normally for as long as it takes to
-- pick a car and the split resumes exactly where it was.
local function suspend(on)
  if not active then return false end
  if on == nil then on = not suspended end
  if on == suspended then return suspended end
  suspended = on

  if suspended then
    ui_visibility.setCef(true)
  elseif cfg.hideUi then
    ui_visibility.setCef(false)
  end
  log('I', 'beamsplit', suspended and 'suspended -- stock UI usable, split paused'
    or 'resumed')
  return suspended
end

local function isSuspended() return suspended end

local function disable()
  if not active then return end
  active = false
  lifecycle = nil
  seatGuard = nil
  for _, v in ipairs(views) do destroyView(v) end
  views = {}
  playerTags.destroy(); taggedCount = nil
  suspended = false
  pendingSpawns = 0
  menu.toggle(false)
  menu.reset()
  hud.reset()
  picker.reset()
  updatePickerActionFilter()
  blockStockSplitActions(false)
  restoreDevices()
  settings.setValue('multiseat', false)
  -- End Session hands the window completely back to stock BeamNG. Its pause menu, map, CEF HUD
  -- and ordinary controller actions must work until the one explicit restart chord is pressed.
  ui_visibility.setCef(true)
  ui_visibility.setImgui(true)
  uiHidden = false
  lastW, lastH = 0, 0
  log('I', 'beamsplit', 'split-screen disabled')
end

local function enable(count, opts)
  count = math.max(1, math.min(MAX_PLAYERS, math.floor(tonumber(count) or cfg.count)))
  for k, val in pairs(opts or {}) do cfg[k] = val end
  cfg.count = count

  if active then disable() end

  -- multiseat does the input half: gamepads -> player indices, players -> vehicles
  settings.setValue('multiseat', true)

  views = {}
  for p = 0, count - 1 do
    local v = {player = p, id = viewName(p), name = viewName(p)}
    views[#views + 1] = v
    createView(v) -- createContext also re-routes player p's look input here
  end

  active = true
  blockStockSplitActions(true)
  assignDevices() -- needs `views` populated: it clamps surplus pads to the last tile
  local vp = im.GetMainViewport()
  relayout(vp.Size.x, vp.Size.y)

  for _, v in ipairs(views) do
    core_camera.setContextCamera(v.id, 'orbit')
  end

  local short = reseat()
  if short and short > 0 then
    log('I', 'beamsplit', short .. ' player(s) need a car -- creating default split vehicles')
  end
  rebuildTags()
  -- CEF only. ui_visibility.set(false) would also call Engine.imgui.setEnabled(false), and the
  -- tile compositor *is* an imgui overlay -- that switches off the thing drawing the split.
  if cfg.hideUi and not uiHidden then ui_visibility.setCef(false); uiHidden = true end
  ui_visibility.setImgui(true)
  log('I', 'beamsplit', 'split-screen enabled for ' .. count .. ' players (' .. tostring(cfg.mode) .. ')')
  if short and short > 0 then spawnCarsForPlayers() end
end

local function setLayout(mode)
  cfg.mode = mode
  cfg.rects = nil -- an in-game layout choice intentionally replaces the launcher preset
  if active then relayout(lastW, lastH) end
end

local function setRenderScale(scale)
  cfg.renderScale = math.max(0.25, math.min(2.0, tonumber(scale) or 1.0))
  if active then
    for _, v in ipairs(views) do v.resW = nil; applyResolution(v) end
  end
end

-- Called by ge/server/commands.lua toggleCamera(): a split-screen player toggles the free cam
-- in their own view only. Returning false lets the stock main-view behaviour run.
local function toggleFreeCam(player)
  if not active then return false end
  local v = views[(player or 0) + 1]
  if not v then return false end
  local state = core_camera.getContextCameraState(v.id)
  if state and state.cam == 'free' then
    core_camera.setContextCamera(v.id, v.prevCam or 'orbit')
  else
    v.prevCam = state and state.cam or 'orbit'
    core_camera.setContextCamera(v.id, 'free')
  end
  return true
end

-- Hooks ----------------------------------------------------------------------

local function onUpdate(dtReal)
  if not active then return end
  if lifecycle then
    updateLifecycle(dtReal)
    if lifecycle then
      lifecycleComposite()
      return
    end
  end
  if suspended then
    -- no overlay in the way, so the menu can go straight down
    menu.draw(M)
    return
  end
  if seatGuard then
    seatGuard.remaining = seatGuard.remaining - (dtReal or 0)
    seatGuard.nextCheck = seatGuard.nextCheck - (dtReal or 0)
    if seatGuard.nextCheck <= 0 then
      enforceExclusiveSeats(seatGuard.player, seatGuard.vehicleId, seatGuard.seats)
      seatGuard.nextCheck = 0.15
    end
    if seatGuard.remaining <= 0 then seatGuard = nil end
  end
  local vp = im.GetMainViewport()
  local w, h = vp.Size.x, vp.Size.y
  if w ~= lastW or h ~= lastH then relayout(w, h) end
  local drawn = composite()
  -- AFTER the compositor, never before: the compositor is a full-window opaque imgui window, so a
  -- menu submitted ahead of it is painted straight over and the only visible effect of opening it
  -- is the cursor unlocking, which reads as the game freezing.
  menu.draw(M)
  if cfg.debug then debugComposite(dtReal or 0, drawn) end
  if taggedCount then playerTags.updatePositions() end

end

-- multiseat re-seats players on its own; make sure the freshly-entered vehicle's camera is
-- selected in that player's view rather than leaving the context on a stale vehicle.
local function onVehicleSwitched(oldId, newId, player)
  if not active or player == nil then return end
  if lifecycle then
    local assignedAlready = lifecycle.assignPlayer == player and lifecycle.switched[player] ~= nil
    if lifecycle.ran and not assignedAlready and newId and newId >= 0 then
      lifecycle.switched[player] = newId
    end
    return
  end
  local v = views[player + 1]
  if v then core_camera.setContextCamera(v.id, 'orbit') end
end

-- a freshly spawned car is the one thing that can resolve a player stuck sharing a seat
local function onVehicleSpawned(vehicleId)
  if pendingSpawns > 0 then pendingSpawns = pendingSpawns - 1 end
  if lifecycle and lifecycle.assignPlayer ~= nil and vehicleId and not lifecycle.targetVehicleId
      and not lifecycle.switched[lifecycle.assignPlayer] then
    lifecycle.switched[lifecycle.assignPlayer] = vehicleId
  end
  if lifecycle and lifecycle.ran and lifecycle.waitForVehicles and pendingSpawns <= 0 then
    lifecycle.postRemaining = lifecycle.postDelay
  elseif active and not lifecycle then
    reseat()
  end
end

M.enable = enable
M.disable = disable
M.isActive = isActive
M.setLayout = setLayout
M.setRenderScale = setRenderScale
M.toggleFreeCam = toggleFreeCam
M.reseat = reseat
M.menu = function(on)
  local result = menu.toggle(on)
  updatePickerActionFilter()
  return result
end
M.isMenuOpen = menu.isOpen
M.suspend = suspend
M.isSuspended = isSuspended
M.nextCar = nextCar
M.resetPlayerVehicle = resetPlayerVehicle
M.pickerToggle = function(player)
  if menu.isOpen() then M.menu(false) end
  local result = picker.toggle(player or 0)
  updatePickerActionFilter()
  return result
end
M.pickerConfirm = function(player)
  if menu.isOpen() then return menu.confirm(M) end
  local result = picker.confirm(player or 0, replacePlayerVehicle)
  updatePickerActionFilter()
  return result
end
M.replacePlayerVehicle = replacePlayerVehicle
-- d-pad navigation for the open picker; returns false when that player has none open, so a
-- binding can fall through to whatever it otherwise does
M.pickerMove = function(player, dir)
  if menu.isOpen() then return menu.move(dir, M) end
  return picker.move(player or 0, dir)
end
M.pickerClose = function(player)
  if menu.isOpen() then M.menu(false); return true end
  local result = picker.close(player or 0)
  updatePickerActionFilter()
  return result
end
M.spawnCarsForPlayers = spawnCarsForPlayers
M.spawnVehicleSafely = spawnVehicleSafely
M.captureScreenshot = captureScreenshot
M.isLifecycleBusy = function() return lifecycle ~= nil end
M.getLifecycleState = function()
  if not lifecycle then return nil end
  return {
    reason = lifecycle.reason,
    targeted = lifecycle.targeted,
    assignPlayer = lifecycle.assignPlayer,
    ran = lifecycle.ran,
  }
end
M.getDrawErrors = function() return compositeErrors end
M.getConfig = function() return deepcopy(cfg) end
M.setConfig = function(opts)
  for k, val in pairs(opts or {}) do cfg[k] = val end
  if active then relayout(lastW, lastH) end -- gutter/mode changes need new rects
end

M.onUpdate = onUpdate
-- core_camera drives the main camera from onPreRender (M.onPreRender = cameraUpdate.onPreRender),
-- which runs after onUpdate -- so placing the listener from onUpdate gets overwritten every frame.
-- Same hook, later in the list, is what makes the write stick.
M.onPreRender = function() if active and not lifecycle then updateListener() end end
M.onVehicleSwitched = onVehicleSwitched
M.onVehicleSpawned = onVehicleSpawned
M.onExtensionUnloaded = function() disable() end
M.onSerialize = function() disable(); playerTags.reset() end

return M
