-- BeamSplit v2 -- polished session overlay.
-- Start opens this shared controller-first hub; Back opens each player's own car picker directly.

local M = {}
local im = ui_imgui

local open = false
local justOpened = false
local focus = 1
local scaleRef = im.FloatPtr(1.0)
local layouts = {'rows', 'columns', 'grid'}

local function showCursor(on)
  lockMouse(not on)
  local c = scenetree.findObject('Canvas')
  if c then c:setCursorVisible(on) end
end

local function currentCarName(player)
  local veh = getPlayerVehicle(player)
  if not veh then return 'Choose a vehicle' end
  local model = core_vehicles.getModel(veh:getJBeamFilename())
  return tostring(model and model.model and model.model.Name or veh:getJBeamFilename() or 'Vehicle')
end

local function cycleLayout(split, dir)
  local cfg = split.getConfig()
  local index = 1
  for i, mode in ipairs(layouts) do if mode == cfg.mode then index = i end end
  index = ((index - 1 + (dir or 1)) % #layouts) + 1
  split.setLayout(layouts[index])
end

local function cycleAudio(split, dir)
  local cfg = split.getConfig()
  local values = {'native', 'center'}
  for p = 0, cfg.count - 1 do values[#values + 1] = p end
  local index = 1
  for i, value in ipairs(values) do if value == cfg.audio then index = i end end
  index = ((index - 1 + (dir or 1)) % #values) + 1
  split.setConfig({audio = values[index]})
end

local function itemCount(split) return split.getConfig().count + 5 end

function M.isOpen() return open end
function M.reset() open = false; justOpened = false; focus = 1 end

function M.toggle(on)
  if on == nil then on = not open end
  if on == open then return open end
  open = on
  justOpened = open
  if open then focus = 1 end
  showCursor(open)
  return open
end

function M.move(dir, split)
  if not open then return false end
  local count = split.getConfig().count
  local layoutItem, audioItem = count + 1, count + 3
  if dir == 'left' or dir == 'right' then
    local step = dir == 'left' and -1 or 1
    if focus == layoutItem then cycleLayout(split, step)
    elseif focus == audioItem then cycleAudio(split, step)
    else focus = ((focus - 1 + step) % itemCount(split)) + 1 end
  else
    local step = (dir == 'up' or dir == -1) and -1 or 1
    focus = ((focus - 1 + step) % itemCount(split)) + 1
  end
  return true
end

function M.confirm(split)
  if not open then return false end
  local cfg = split.getConfig()
  local count = cfg.count
  if focus <= count then
    local player = focus - 1
    split.menu(false)
    split.pickerToggle(player)
  elseif focus == count + 1 then
    cycleLayout(split, 1)
  elseif focus == count + 2 then
    split.setConfig({showHud = not cfg.showHud})
  elseif focus == count + 3 then
    cycleAudio(split, 1)
  elseif focus == count + 4 then
    split.menu(false)
  else
    split.menu(false)
    split.disable()
  end
  return true
end

local function focusedButton(label, index, width, height)
  if focus == index then
    im.PushStyleColor2(im.Col_Button, im.ImVec4(0.12, 0.42, 0.72, 1))
    im.PushStyleColor2(im.Col_ButtonHovered, im.ImVec4(0.16, 0.52, 0.88, 1))
  end
  local pressed = im.Button(label, im.ImVec2(width, height))
  if focus == index then im.PopStyleColor(2) end
  if pressed then focus = index end
  return pressed
end

function M.draw(split)
  if not open then return end
  local cfg = split.getConfig()
  local viewport = im.GetMainViewport()
  local width, height = 760, 720
  im.SetNextWindowSize(im.ImVec2(width, height))
  im.SetNextWindowViewport(viewport.ID)
  im.SetNextWindowPos(im.ImVec2(viewport.Pos.x + (viewport.Size.x - width) * 0.5,
    viewport.Pos.y + (viewport.Size.y - height) * 0.5))
  if justOpened then im.SetNextWindowFocus(); justOpened = false end

  im.PushStyleVar1(im.StyleVar_WindowRounding, 18)
  im.PushStyleVar1(im.StyleVar_WindowBorderSize, 1)
  im.PushStyleVar2(im.StyleVar_WindowPadding, im.ImVec2(28, 24))
  im.PushStyleVar2(im.StyleVar_ItemSpacing, im.ImVec2(12, 12))
  im.PushStyleColor2(im.Col_WindowBg, im.ImVec4(0.035, 0.045, 0.06, 0.97))
  im.PushStyleColor2(im.Col_Border, im.ImVec4(0.18, 0.42, 0.68, 0.75))
  im.PushStyleColor2(im.Col_Button, im.ImVec4(0.09, 0.12, 0.16, 1))
  im.PushStyleColor2(im.Col_ButtonHovered, im.ImVec4(0.13, 0.22, 0.31, 1))

  local stillOpen = im.BoolPtr(true)
  if im.Begin('BeamSplit Session##beamsplitMenu', stillOpen,
      bit.bor(im.WindowFlags_NoResize, im.WindowFlags_NoCollapse, im.WindowFlags_NoSavedSettings)) then
    im.SetWindowFontScale(0.82)
    im.TextColored(im.ImVec4(0.34, 0.68, 1, 1), 'B E A M S P L I T')
    im.SetWindowFontScale(1.45)
    im.Text('Split-screen session')
    im.SetWindowFontScale(1)
    im.TextColored(im.ImVec4(1, 1, 1, 0.56),
      'Every player owns a view, a controller and a car.  D-pad navigate  |  A select  |  B close')
    im.Separator()

    im.TextColored(im.ImVec4(1, 1, 1, 0.72), 'PLAYERS')
    for i = 1, cfg.count do
      local player = i - 1
      local label = string.format('P%d   %s\n       Choose this player\'s car##player%d',
        i, currentCarName(player), i)
      if focusedButton(label, i, 700, 62) then M.confirm(split) end
    end

    im.TextColored(im.ImVec4(1, 1, 1, 0.72), 'SESSION')
    local base = cfg.count
    local layoutLabel = string.format('Layout                         <  %s  >##layout',
      tostring(cfg.mode):upper())
    if focusedButton(layoutLabel, base + 1, 340, 42) then M.confirm(split) end
    im.SameLine()
    local hudLabel = 'Player HUD                     ' .. (cfg.showHud and 'ON' or 'OFF') .. '##hud'
    if focusedButton(hudLabel, base + 2, 348, 42) then M.confirm(split) end

    local audio = cfg.audio == 'native' and 'NATIVE / PLAYER 1'
      or (cfg.audio == 'center' and 'BETWEEN PLAYERS'
      or (type(cfg.audio) == 'number' and ('PLAYER ' .. (cfg.audio + 1)) or 'NATIVE / PLAYER 1'))
    local audioLabel = 'Audio focus                    <  ' .. audio .. '  >##audio'
    if focusedButton(audioLabel, base + 3, 700, 42) then M.confirm(split) end

    scaleRef[0] = cfg.renderScale
    if im.SliderFloat('Tile render quality', scaleRef, 0.5, 1.25, '%.2f') then
      split.setRenderScale(scaleRef[0])
    end

    im.Separator()
    if focusedButton('BACK TO DRIVING##close', base + 4, 480, 46) then M.confirm(split) end
    im.SameLine()
    if focusedButton('END SESSION##end', base + 5, 208, 46) then M.confirm(split) end
  end
  im.End()
  im.PopStyleColor(4)
  im.PopStyleVar(4)

  if not stillOpen[0] then split.menu(false) end
end

return M

