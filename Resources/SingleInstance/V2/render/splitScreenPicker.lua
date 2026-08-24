-- BeamSplit v2 -- per-player vehicle picker.
--
-- The stock selector is a CEF screen: one instance, full window, driven by player 0 and mouse-only.
-- That is wrong twice over for split-screen -- it cannot be opened per tile, and nobody on a
-- gamepad can use it. So each player gets their own card grid, drawn in their own tile, driven
-- entirely by their own pad.
--
-- The first grid contains models. Confirming one drills into that model's complete preset list,
-- including mod presets, using core_vehicles.getModel(model).configs. Both levels carry the game's
-- resolved preview paths, and thumbnails are loaded lazily for only the visible page.
--
-- Navigation is a plain focus index with wrapping rows, so a d-pad reaches everything: left/right
-- steps a column, up/down steps a row, OK swaps the car, Back closes. Nothing here is
-- mouse-reachable by design; the mouse-driven admin menu is splitScreenMenu.lua.

local M = {}
local im = ui_imgui
local imUtils = require('ui/imguiUtils')

local COLS, ROWS = 4, 2          -- cards visible at once
local CARD_W, CARD_H = 168, 116  -- card box; the thumbnail fills the top, label sits under it
local GAP = 12
local LABEL_H = 22

local models = nil               -- sorted {key, name, preview}
local presets = {}               -- model key -> sorted {key, name, preview, modelKey}
local textures = {}              -- unique model/config key -> texObj (lazy)
local state = {}                 -- [player] = {stage, index, modelIndex, modelKey}

-- core_vehicles.getModelList()'s shape has moved between versions, so read it defensively and
-- fall back to the raw key rather than assuming a display-name field exists.
local function loadModels()
  if models then return models end
  local list = core_vehicles.getModelList()
  local src = (type(list) == 'table' and (list.models or list)) or {}
  models = {}
  for key, data in pairs(src) do
    if type(key) == 'string' then
      local info = (type(data) == 'table' and data) or {}
      local kind = info.Type
      if kind == nil or kind == 'Car' or kind == 'Truck' then
        models[#models + 1] = {
          key = key,
          name = tostring(info.Name or key),
          -- getModelList resolves this to the file that exists; fall back to the conventional
          -- path so a model whose data lacks the field still gets a thumbnail attempt
          preview = info.preview or ('/vehicles/' .. key .. '/default.jpg'),
        }
      end
    end
  end
  table.sort(models, function(a, b) return a.name:lower() < b.name:lower() end)
  log('I', 'beamsplit', 'vehicle picker loaded ' .. #models .. ' models')
  return models
end

local function loadPresets(model)
  if presets[model.key] then return presets[model.key] end
  local data = core_vehicles.getModel(model.key)
  local src = (type(data) == 'table' and data.configs) or {}
  local result = {}
  for key, info in pairs(src) do
    if type(info) == 'table' then
      local configKey = tostring(info.key or key)
      result[#result + 1] = {
        key = configKey,
        modelKey = model.key,
        name = tostring(info.Configuration or info.Name or configKey),
        preview = info.preview or ('/vehicles/' .. model.key .. '/' .. configKey .. '.jpg'),
        isDefault = info.is_default_config == true,
      }
    end
  end
  table.sort(result, function(a, b)
    if a.isDefault ~= b.isDefault then return a.isDefault end
    return a.name:lower() < b.name:lower()
  end)
  -- A malformed or very old mod vehicle may not publish config metadata. Keep it usable by
  -- offering BeamNG's default configuration instead of trapping the player on an empty page.
  if #result == 0 then
    result[1] = {
      key = nil,
      modelKey = model.key,
      name = 'Default',
      preview = model.preview,
      isDefault = true,
    }
  end
  presets[model.key] = result
  log('I', 'beamsplit', 'vehicle picker loaded ' .. #result .. ' presets for ' .. model.key)
  return result
end

local function currentList(s)
  if s.stage == 'presets' then
    local model = loadModels()[s.modelIndex]
    return model and loadPresets(model) or {}
  end
  return loadModels()
end

-- lazily built, and only for what is on screen: loading every texture up front hitches the frame
local function thumb(entry)
  local textureKey = (entry.modelKey or 'model') .. ':' .. tostring(entry.key or 'default')
  if textures[textureKey] ~= nil then return textures[textureKey] or nil end
  local ok, tex = pcall(imUtils.texObj, entry.preview)
  textures[textureKey] = (ok and tex and tex.texId) and tex or false
  return textures[textureKey] or nil
end

function M.reset() state = {}; models = nil; presets = {}; textures = {} end
function M.isOpen(player) return state[player] ~= nil end

function M.toggle(player)
  if state[player] then state[player] = nil; return false end
  if #loadModels() == 0 then
    log('E', 'beamsplit', 'vehicle picker: no models available')
    return false
  end
  -- open on the car the player is already in, so Back-then-OK is a no-op rather than a surprise
  local veh = getPlayerVehicle(player)
  local jb = veh and veh:getJBeamFilename()
  local index = 1
  for i, m in ipairs(loadModels()) do
    if m.key == jb then index = i; break end
  end
  state[player] = {stage = 'models', index = index, modelIndex = index}
  return true
end

-- dir: 'left' | 'right' | 'up' | 'down', or +/-1 for a flat step
function M.move(player, dir)
  local s = state[player]
  if not s then return false end
  local n = #currentList(s)
  if n == 0 then return false end
  local step = 1
  if dir == 'left' then step = -1
  elseif dir == 'right' then step = 1
  elseif dir == 'up' then step = -COLS
  elseif dir == 'down' then step = COLS
  elseif type(dir) == 'number' then step = dir end
  s.index = ((s.index - 1 + step) % n) + 1
  return true
end

-- First confirmation opens that model's presets; second confirmation replaces only this player's
-- explicit vehicle with the selected model/configuration.
function M.confirm(player, replaceFn)
  local s = state[player]
  if not s then return false end
  if s.stage == 'models' then
    local model = loadModels()[s.index]
    if not model then return false end
    local list = loadPresets(model)
    s.stage = 'presets'
    s.modelIndex = s.index
    s.modelKey = model.key
    s.index = 1
    for i, config in ipairs(list) do
      if config.isDefault then s.index = i; break end
    end
    return true
  end

  local entry = currentList(s)[s.index]
  state[player] = nil
  if not entry then return false end
  local veh = getPlayerVehicle(player)
  if not veh and not replaceFn then
    log('E', 'beamsplit', 'vehicle picker: player ' .. player .. ' has no car to replace')
    return false
  end
  local accepted
  if replaceFn then accepted = replaceFn(entry.modelKey, entry.key, veh, player)
  else accepted = core_vehicles.replaceVehicle(entry.modelKey, {config = entry.key}, veh) end
  if accepted == false then
    log('E', 'beamsplit', 'vehicle picker: replacement could not be scheduled')
    return false
  end
  log('I', 'beamsplit', 'player ' .. player .. ' picked ' .. entry.modelKey .. '/' .. tostring(entry.key))
  return true
end

function M.close(player)
  local s = state[player]
  if not s then return false end
  if s.stage == 'presets' then
    s.stage = 'models'
    s.index = s.modelIndex or 1
    s.modelKey = nil
    return true
  end
  state[player] = nil
  return true
end

local function u32(r, g, b, a) return im.GetColorU322(im.ImVec4(r, g, b, a or 1)) end

local PANEL  = u32(13 / 255, 15 / 255, 17 / 255, 0.86)
local CARD   = u32(1, 1, 1, 0.06)
local INK    = u32(244 / 255, 246 / 255, 247 / 255, 1)
local INK_60 = u32(244 / 255, 246 / 255, 247 / 255, 0.6)
local HAIR   = u32(1, 1, 1, 0.14)

local function text(dl, x, y, sizePx, col, str, centreW)
  if centreW then x = x + (centreW - #str * sizePx * 0.54) * 0.5 end
  im.SetWindowFontScale(sizePx / math.max(1, im.GetFontSize()))
  im.SetCursorScreenPos(im.ImVec2(x, y))
  im.PushStyleColor1(im.Col_Text, col)
  im.TextUnformatted(str)
  im.PopStyleColor(1)
  im.SetWindowFontScale(1)
end

function M.draw(dl, tile, player, color)
  local s = state[player]
  if not s then return end
  local list = currentList(s)
  local accent = u32(color[1], color[2], color[3], 1)

  -- one scale for the grid, so a quadrant tile gets a smaller grid rather than a clipped one
  local sc = math.max(0.55, math.min(1.0, math.min(tile.w / 900, tile.h / 620)))
  local cw, ch, gap = CARD_W * sc, CARD_H * sc, GAP * sc
  local gridW = COLS * cw + (COLS - 1) * gap
  local gridH = ROWS * ch + (ROWS - 1) * gap
  local padX, headH = 18 * sc, 34 * sc

  local x0 = tile.x + (tile.w - gridW) * 0.5
  local y0 = tile.y + (tile.h - gridH) * 0.5

  im.ImDrawList_AddRectFilled(dl, im.ImVec2(x0 - padX, y0 - headH - padX * 0.5),
    im.ImVec2(x0 + gridW + padX, y0 + gridH + padX), PANEL, 8 * sc, 0)
  im.ImDrawList_AddRect(dl, im.ImVec2(x0 - padX, y0 - headH - padX * 0.5),
    im.ImVec2(x0 + gridW + padX, y0 + gridH + padX), HAIR, 8 * sc, 0, 1)

  im.ImDrawList_AddRectFilled(dl, im.ImVec2(x0 - padX, y0 - headH - padX * 0.5),
    im.ImVec2(x0 - padX + 4 * sc, y0 + gridH + padX), accent, 8 * sc, 0)
  local model = loadModels()[s.modelIndex or s.index]
  local title = s.stage == 'presets' and ('P' .. (player + 1) .. '  ' .. (model and model.name or 'pick a preset'))
    or ('P' .. (player + 1) .. '  pick a car')
  local hint = s.stage == 'presets' and 'D-pad  move    A  spawn    B  cars'
    or 'D-pad  move    A  presets    B  close'
  text(dl, x0, y0 - headH, 15 * sc, INK, title)
  text(dl, x0 + gridW - 205 * sc, y0 - headH + 2 * sc, 11 * sc, INK_60, hint)

  -- page containing the focused card, so the selection is always on screen
  local perPage = COLS * ROWS
  local page = math.floor((s.index - 1) / perPage)
  for slot = 0, perPage - 1 do
    local idx = page * perPage + slot + 1
    local entry = list[idx]
    if entry then
      local c, r = slot % COLS, math.floor(slot / COLS)
      local cx = x0 + c * (cw + gap)
      local cy = y0 + r * (ch + gap)
      local focused = (idx == s.index)

      im.ImDrawList_AddRectFilled(dl, im.ImVec2(cx, cy), im.ImVec2(cx + cw, cy + ch), CARD, 5 * sc, 0)

      local tex = thumb(entry)
      local imgH = ch - LABEL_H * sc
      if tex then
        im.ImDrawList_AddImage(dl, tex.texId, im.ImVec2(cx + 2, cy + 2),
          im.ImVec2(cx + cw - 2, cy + imgH), im.ImVec2(0, 0), im.ImVec2(1, 1), u32(1, 1, 1, 1))
      else
        text(dl, cx, cy + imgH * 0.4, 11 * sc, INK_60, 'no image', cw)
      end

      text(dl, cx, cy + imgH + 3 * sc, 12 * sc, focused and INK or INK_60, entry.name, cw)

      if focused then
        im.ImDrawList_AddRect(dl, im.ImVec2(cx - 2, cy - 2), im.ImVec2(cx + cw + 2, cy + ch + 2),
          accent, 6 * sc, 0, 3 * sc)
      end
    end
  end

  text(dl, x0, y0 + gridH + 4 * sc, 11 * sc, INK_60,
    string.format('%d / %d', s.index, #list))
end

return M
