-- BeamSplit v2 -- mod entry point.
-- Intentionally does NOT auto-load render_splitScreen: core/multiseat.lua hands the player tags
-- over to us the moment the extension is loaded, so we only load it when split-screen is asked for.
-- Console (backtick) usage:
--   beamsplit.on(2)          -- 2-player split, auto layout
--   beamsplit.on(4, {mode='grid', renderScale=0.85})
--   beamsplit.off()

local recovering = {}

local function load()
  if not extensions.isExtensionLoaded('render_splitScreen') then
    extensions.load('render_splitScreen')
    setExtensionUnloadMode(extensions.render_splitScreen, 'manual')
  end
  return extensions.render_splitScreen
end

beamsplit = {
  on = function(count, opts) return load().enable(count or 2, opts) end,
  off = function()
    if extensions.isExtensionLoaded('render_splitScreen') then extensions.render_splitScreen.disable() end
  end,
  -- bound to the "Toggle split-screen" input action; defaults to as many players as there are pads
  toggle = function(count)
    local ext = load()
    if ext.isActive() then return ext.disable() end
    local saved = ext.getConfig()
    return ext.enable(count or saved.count or beamsplit.suggestedPlayerCount())
  end,
  -- one player per non-keyboard/mouse device, clamped to what the mod supports
  suggestedPlayerCount = function()
    local pads = 0
    for devname in pairs(core_input_bindings.devices or {}) do
      if not (devname:startswith('keyboard') or devname:startswith('mouse')) then pads = pads + 1 end
    end
    return math.max(2, math.min(4, pads))
  end,
  -- spawn/seat one car per player; model defaults to whatever player 0 is driving
  cars = function(model) return load().spawnCarsForPlayers(model) end,
  -- bound per player via the Multiseat action map: PLAYER is substituted per device
  nextCar = function(player, dir) return load().nextCar(player or 0, dir or 1) end,
  -- BeamSplit's own menu: players, cars, layout, audio, HUD -- drawn over the running split
  menu = function(on)
    local ext = load()
    if not ext.isActive() then return false end
    return ext.menu(on)
  end,
  -- The custom Start binding still owns the button after End Session, so there is no
  -- binding fall-through to BeamNG. Forward both edges through BeamNG's exact stock
  -- menu navigation route while inactive; during a split only the press toggles our UI.
  menuInput = function(value)
    value = value or 0
    local ext = load()
    if ext.isActive() then
      if value > 0 then return ext.menu() end
      return true
    end
    if ui_visibility then
      ui_visibility.setCef(true)
      ui_visibility.setImgui(true)
    end
    guihooks.trigger('UINavigation', 'menu', value)
    return true
  end,
  -- step out of the split so BeamNG's own menus (vehicle selector, parts, options) are usable,
  -- then call it again to drop back in; the split is paused, not torn down
  stockMenu = function(on) return load().suspend(on) end,
  picker = function(player)
    local ext = load()
    if not ext.isActive() then return false end
    return ext.pickerToggle(player or 0)
  end,
  -- d-pad navigation inside a player's own picker: 'left' 'right' 'up' 'down'
  nav = function(player, dir)
    local ext = load()
    if not ext.isActive() then return false end
    player = player or 0
    if ext.pickerMove(player, dir) then return true end
    -- D-pad left is BeamNG's normal per-player recovery whenever no BeamSplit UI consumes it.
    if dir == 'left' then
      local handled = ext.resetPlayerVehicle(player, true)
      recovering[player] = handled == true
      return handled
    end
    return false
  end,
  navRelease = function(player, dir)
    player = player or 0
    if dir ~= 'left' or not recovering[player] then return false end
    recovering[player] = nil
    return load().resetPlayerVehicle(player, false)
  end,
  navBack = function(player)
    local ext = load()
    return ext.isActive() and ext.pickerClose(player or 0) or false
  end,
  pickerConfirm = function(player)
    local ext = load()
    return ext.isActive() and ext.pickerConfirm(player or 0) or false
  end,
  -- one listener exists engine-side, so this selects the both-car shared mix, native, or a player camera
  cycleAudio = function()
    local ext = load()
    local cur = ext.getConfig().audio
    local n = ext.getConfig().count or 2
    local values = {'shared', 'native'}
    for p = 0, n - 1 do values[#values + 1] = p end
    local index = 1
    for i, value in ipairs(values) do if value == cur then index = i end end
    local next = values[index % #values + 1]
    ext.setConfig({audio = next})
    log('I', 'beamsplit', 'audio listener: ' .. tostring(next))
    return next
  end,
  toggleHud = function()
    local ext = load()
    local on = not ext.getConfig().showHud
    ext.setConfig({showHud = on})
    return on
  end,
  layout = function(mode) return load().setLayout(mode) end,
  cycleLayout = function()
    local ext = load()
    if not ext.isActive() then return end
    local modes = {'rows', 'columns', 'grid'}
    local cur = ext.getConfig().mode
    local i = 1
    for n, m in ipairs(modes) do if m == cur then i = n end end
    local next = modes[i % #modes + 1]
    ext.setLayout(next)
    guihooks.trigger('toastrMsg', {type = 'info', title = 'BeamSplit', msg = 'Layout: ' .. next})
  end,
  scale = function(s) return load().setRenderScale(s) end,
  status = function()
    if not extensions.isExtensionLoaded('render_splitScreen') then return 'not loaded' end
    return dumps({active = extensions.render_splitScreen.isActive(), cfg = extensions.render_splitScreen.getConfig()})
  end,
}

log('I', 'beamsplit', 'BeamSplit v2 ready -- use beamsplit.on(2) / beamsplit.off() in the console')

-- The desktop launcher supplies /settings/beamsplit/session.json. Its tiny bridge waits for a
-- Freeroam vehicle, then starts this exact backend with the launcher's screen and pad assignments.
if not extensions.isExtensionLoaded('beamsplit_launcher') then
  extensions.load('beamsplit_launcher')
  setExtensionUnloadMode(extensions.beamsplit_launcher, 'manual')
end

-- Unattended smoke test. `-onLevelLoad_ext` is parsed before the mod filesystem is mounted, so
-- the extension does not exist yet at that point; instead the launcher sets a global early via
-- `-lua "beamsplitSelftest = true"` and we pick it up here, once mods are mounted.
if beamsplitSelftest then
  log('I', 'beamsplit', 'selftest requested -- loading beamsplit_selftest')
  extensions.load('beamsplit_selftest')
  setExtensionUnloadMode(extensions.beamsplit_selftest, 'manual')
end
