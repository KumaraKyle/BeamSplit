-- BeamSplit v2 -- unattended smoke test.
-- Launch with:  BeamNG.drive.x64.exe -level gridmap_v2 -onLevelLoad_ext beamsplit_selftest
-- Spawns a second car, seats player 1 in it directly (so the test does not need a second
-- gamepad), turns on a 2-player split, screenshots the result and writes a verdict to the log.

local M = {}

local LOG = 'beamsplit.selftest'
local t = 0
local phase = 1
local results = {}
local targetedOtherVehicleId

local function note(ok, what, detail)
  results[#results + 1] = {ok = ok, what = what, detail = detail}
  log(ok and 'I' or 'E', LOG, (ok and 'PASS  ' or 'FAIL  ') .. what .. (detail and ('  -- ' .. tostring(detail)) or ''))
end

local function try(what, fn)
  local ok, err = pcall(fn)
  note(ok, what, not ok and err or nil)
  return ok
end

local function activeVehicles()
  local res = {}
  for _, veh in activeVehiclesIterator() do res[#res + 1] = veh end
  return res
end

-- the scenario players actually hit: one car on the map, split turned on, both players
-- piled into it by stock multiseat. spawnCarsForPlayers has to fill the gap.
local function fillCars()
  extensions.render_splitScreen.spawnCarsForPlayers()
end

-- each pad must land on its own player, and inside the split (stock multiseat gives the keyboard
-- player 0 and pushes two pads out to players 1 and 2)
local function verifyDeviceAssignment()
  local assigned = core_input_bindings.assignedPlayers or {}
  local pads, seen = {}, {}
  for dev, player in pairs(assigned) do
    if not (dev:startswith('keyboard') or dev:startswith('mouse')) then
      pads[#pads + 1] = dev .. '->player' .. tostring(player)
      if player > 1 then
        error(dev .. ' is player ' .. player .. ', outside the 2-way split')
      end
      if seen[player] then
        error(seen[player] .. ' and ' .. dev .. ' are both player ' .. player)
      end
      seen[player] = dev
    end
  end
  if #pads == 0 then error('no gamepads detected -- plug them in before launching') end
  table.sort(pads)
  log('I', LOG, 'pads: ' .. table.concat(pads, ', '))
end

-- with exactly one car per player nobody has anywhere free to go, so the swap test needs a spare
local function spawnSpare()
  local base = getPlayerVehicle(0)
  if not base then error('player 0 has no vehicle') end
  if not extensions.render_splitScreen.spawnVehicleSafely(base:getJBeamFilename(),
      {pos = base:getPosition() + vec3(0, 9, 0), rot = quat(0, 0, 0, 1)}) then
    error('spare vehicle could not be scheduled')
  end
end

-- a player swapping cars must land somewhere new, and must not land on another player's car
local function verifySwap()
  local before = getPlayerVehicle(1)
  if not before then error('player 1 has no vehicle to swap from') end
  local otherBefore = getPlayerVehicle(0) and getPlayerVehicle(0):getID()

  if not extensions.render_splitScreen.nextCar(1, 1) then
    error('nextCar reported no free car to swap to')
  end

  local after = getPlayerVehicle(1)
  if not after then error('player 1 lost their vehicle') end
  if after:getID() == before:getID() then
    error('player 1 did not move (still vehicle ' .. before:getID() .. ')')
  end
  if after:getID() == otherBefore then
    error('player 1 swapped into player 0 car ' .. otherBefore)
  end
  log('I', LOG, string.format('player 1 swapped %d -> %d', before:getID(), after:getID()))
end

-- the SFX listener has no setter and follows the main camera, so this is the real proof that
-- driving the main camera actually moved where the game is listening from
local function verifyListenerCentred()
  local a, b = getPlayerVehicle(0), getPlayerVehicle(1)
  if not (a and b) then error('need both players in cars') end
  local mid = (a:getPosition() + b:getPosition()) / 2
  local lx, ly, lz = SFXSystem.getPositionXYZ()
  local listener = vec3(lx, ly, lz)
  local drift = (listener - mid):length()
  local apart = (a:getPosition() - b:getPosition()):length()
  log('I', LOG, string.format('cars %.1fm apart, listener %.1fm from midpoint', apart, drift))
  -- generous: the cars are moving and the listener is sampled a frame later
  if drift > math.max(3, apart * 0.25) then
    error(string.format('listener is %.1fm off the midpoint (cars %.1fm apart)', drift, apart))
  end
end

-- the HUD silently degrades to "--" on a missing value, so assert the sources it reads are
-- actually resolving rather than trusting that it drew without erroring
local function verifyHudData()
  local vid = getPlayerVehicle(0) and getPlayerVehicle(0):getID()
  if not vid then error('player 0 has no vehicle') end

  local mapObj = map and map.objects and map.objects[vid]
  if not mapObj then error('map.objects has no entry for vehicle ' .. vid) end
  if mapObj.damage == nil then error('map.objects[vid].damage is nil') end

  local get = core_vehicleBridge.getCachedVehicleData
  local missing = {}
  for _, key in ipairs({'rpm', 'fuel', 'fuelVolume', 'fuelCapacity'}) do
    if get(vid, key) == nil then missing[#missing + 1] = key end
  end
  if #missing > 0 then
    error('vehicleBridge has no value yet for: ' .. table.concat(missing, ', '))
  end
  log('I', LOG, string.format('hud data: damage=%.0f rpm=%.0f fuel=%.0f/%.0f L',
    mapObj.damage, get(vid, 'rpm'), get(vid, 'fuelVolume'), get(vid, 'fuelCapacity')))
end

-- the tiles only reach the screen through the imgui layer, so if anything switches it off the
-- player sees the bare main camera even though the compositor is happily drawing
local function verifyImguiAlive()
  if not Engine.imgui.isEnabled() then
    error('imgui layer is disabled -- tiles are being drawn but never shown')
  end
  local ext = extensions.render_splitScreen
  if ext.isLifecycleBusy() then error('renderer-safe operation has not restored the split views yet') end
  if ext.getDrawErrors() > 0 then
    error(ext.getDrawErrors() .. ' compositor draw error(s) -- see the beamsplit log line')
  end
end

-- calling the spawn twice in quick succession must not double-spawn: a car takes seconds to
-- become drivable, so the second call has to count the in-flight one (this is the "3 cars" bug)
local function verifyNoOverSpawn()
  local ext = extensions.render_splitScreen
  local before = #activeVehicles()
  ext.spawnCarsForPlayers()
  ext.spawnCarsForPlayers()
  local after = #activeVehicles()
  if after > before then
    error(string.format('spawned %d extra car(s) when every player already had one', after - before))
  end
  log('I', LOG, 'repeat spawn added no cars (' .. before .. ' on the map)')
end

local function verifyMenu()
  local ext = extensions.render_splitScreen
  if ext.isMenuOpen() then error('menu started open') end
  ext.menu(true)
  if not ext.isMenuOpen() then error('menu did not open') end
  ext.menu(false)
  if ext.isMenuOpen() then error('menu did not close') end
  log('I', LOG, 'menu opened and closed')
end

local function verifyPicker()
  local ext = extensions.render_splitScreen
  if not ext.pickerToggle(1) then error('picker did not open for player 1') end
  if not core_input_actionFilter.isActionBlocked('accept') then
    error('stock A-button actions were not blocked while picker was open')
  end
  for _, dir in ipairs({'right', 'down', 'left', 'up'}) do
    if not ext.pickerMove(1, dir) then error('picker did not accept ' .. dir) end
  end
  ext.pickerClose(1) -- close without applying; replacing a car mid-test would race other checks
  if ext.pickerToggle(1) == false then error('picker would not reopen') end
  ext.pickerClose(1)
  if core_input_actionFilter.isActionBlocked('accept') then
    error('stock A-button actions stayed blocked after picker closed')
  end
  log('I', LOG, 'picker opened, navigated all four directions and closed for player 1')
end

-- a wrong preview path shows silently as "no image" on every card, so check the game actually
-- resolves one: getModelList gives each model a `preview` of /vehicles/<key>/default.(png|jpg)
local function verifyThumbnails()
  local list = core_vehicles.getModelList()
  local src = (type(list) == 'table' and (list.models or list)) or {}
  local checked, withPreview = 0, 0
  for key, data in pairs(src) do
    if type(key) == 'string' and type(data) == 'table' then
      checked = checked + 1
      local path = data.preview or ('/vehicles/' .. key .. '/default.jpg')
      if FS:fileExists(path) then withPreview = withPreview + 1 end
    end
  end
  log('I', LOG, string.format('vehicle previews on disk: %d of %d models', withPreview, checked))
  if withPreview == 0 then error('no vehicle preview images resolved -- cards would all be blank') end
end

local function verifyDistinctCars()
  local seen, byPlayer = {}, {}
  for p = 0, 1 do
    local veh = getPlayerVehicle(p)
    if not veh then error('player ' .. p .. ' has no vehicle') end
    local vid = veh:getID()
    byPlayer[p] = vid
    if seen[vid] then
      error(string.format('players %d and %d share vehicle %d', seen[vid], p, vid))
    end
    seen[vid] = p
  end
  log('I', LOG, string.format('player 0 -> vehicle %d, player 1 -> vehicle %d', byPlayer[0], byPlayer[1]))
end

-- Regression for the vehicle-change freeze: replacing player 1's car must use the targeted
-- lifecycle fence. Player 0's context, seat and input route stay alive throughout the rebuild.
local function startTargetedReplacement()
  local ext = extensions.render_splitScreen
  local target = getPlayerVehicle(1)
  local other = getPlayerVehicle(0)
  if not (target and other) then error('need both player vehicles for targeted replacement') end
  targetedOtherVehicleId = other:getID()
  if not ext.replacePlayerVehicle(target:getJBeamFilename(), nil, target, 1) then
    error('targeted replacement could not be scheduled')
  end
  local state = ext.getLifecycleState()
  if not (state and state.targeted and state.assignPlayer == 1) then
    error('replacement did not enter player-targeted lifecycle')
  end
end

local function verifyUnaffectedPlayerLive()
  local ext = extensions.render_splitScreen
  local state = ext.getLifecycleState()
  if not state then error('targeted lifecycle finished before its isolation could be inspected') end
  if not state.targeted or state.assignPlayer ~= 1 then error('vehicle change fell back to global fence') end
  if not core_camera.getContextCameraState('splitScreenP0') then
    error('unaffected player 0 camera context was detached')
  end
  if core_camera.getContextCameraState('splitScreenP1') then
    error('changing player 1 camera context was not isolated')
  end
  local other = getPlayerVehicle(0)
  if not other or other:getID() ~= targetedOtherVehicleId then
    error('unaffected player 0 lost or changed vehicle')
  end
  if settings.getValue('multiseat') ~= true then error('multiseat input was globally disabled') end
  log('I', LOG, 'player 0 context/seat/input stayed live during player 1 replacement')
end

local function verifyTargetedReplacementRestored()
  local ext = extensions.render_splitScreen
  if ext.isLifecycleBusy() then error('targeted replacement is still settling') end
  if not core_camera.getContextCameraState('splitScreenP0') then error('player 0 context missing after replacement') end
  if not core_camera.getContextCameraState('splitScreenP1') then error('player 1 context was not restored') end
  verifyDistinctCars()
end

local function enableSplit()
  extensions.load('render_splitScreen')
  setExtensionUnloadMode(extensions.render_splitScreen, 'manual')
  extensions.render_splitScreen.enable(2, {debug = true})
  if not extensions.render_splitScreen.isActive() then error('enable() did not set active') end
end

-- the real proof: both RenderViews exist, are sized, and the camera contexts are driving them
local function verifyViews()
  local problems = {}
  for p = 0, 1 do
    local name = 'splitScreenP' .. p
    local rv = RenderViewManagerInstance:getView(name)
    if not rv then
      problems[#problems + 1] = name .. ': no RenderView'
    else
      local res = rv.resolution
      if not res or res.x < 64 or res.y < 64 then
        problems[#problems + 1] = name .. ': bad resolution ' .. dumps(res)
      end
    end
    local state = core_camera.getContextCameraState(name)
    if not state then
      problems[#problems + 1] = name .. ': no camera context state'
    elseif not state.cam then
      problems[#problems + 1] = name .. ': context has no active camera'
    else
      log('I', LOG, name .. ': cam=' .. tostring(state.cam) .. ' fov=' .. tostring(state.fov) ..
        ' pos=' .. string.format('%.1f,%.1f,%.1f', state.pos.x, state.pos.y, state.pos.z))
    end
  end
  if #problems > 0 then error(table.concat(problems, ' | ')) end
end

-- the two views must not be looking from the same place, or we are rendering one camera twice
local function verifyViewsDiffer()
  local a = core_camera.getContextCameraState('splitScreenP0')
  local b = core_camera.getContextCameraState('splitScreenP1')
  if not (a and b) then error('missing context state') end
  local d = math.sqrt((a.pos.x - b.pos.x) ^ 2 + (a.pos.y - b.pos.y) ^ 2 + (a.pos.z - b.pos.z) ^ 2)
  log('I', LOG, string.format('camera separation: %.2f m', d))
  if d < 0.5 then error(string.format('views are %.2f m apart -- same camera in both tiles?', d)) end
end

local function summarise()
  local failed = 0
  for _, r in ipairs(results) do if not r.ok then failed = failed + 1 end end
  log('I', LOG, '================ BEAMSPLIT SELFTEST ================')
  for _, r in ipairs(results) do
    log(r.ok and 'I' or 'E', LOG, (r.ok and '  PASS  ' or '  FAIL  ') .. r.what .. (r.detail and ('  -- ' .. tostring(r.detail)) or ''))
  end
  log(failed == 0 and 'I' or 'E', LOG, '================ ' .. (#results - failed) .. '/' .. #results ..
    ' passed ================')
end

-- Phases open on a clock, because the level and the first RenderView frames just need to settle.
-- Anything downstream of a vehicle spawn also gets `retryFor`: a spawned car can take ten seconds
-- or more to become drivable on a cold level, and asserting on a fixed clock turns that latency
-- into a spurious failure.
local phases = {
  {at = 6,  what = 'enable 2-player split',          fn = enableSplit},
  {at = 9,  what = 'spawn a car per player',         fn = fillCars},
  {at = 13, what = 'each pad drives its own player', fn = verifyDeviceAssignment},
  {at = 14, what = 'each player has their own car',  fn = verifyDistinctCars, retryFor = 30},
  {at = 15, what = 'render views created and sized', fn = verifyViews,        retryFor = 10},
  {at = 16, what = 'views have independent cameras', fn = verifyViewsDiffer,  retryFor = 30},
  {at = 17, what = 'spawn a spare car',              fn = spawnSpare},
  {at = 22, what = 'player swaps to a free car',     fn = verifySwap,         retryFor = 30},
  -- ge/screenshot.lua is required into the global `screenshot`, it is not an extension
  {at = 24, what = 'audio listener sits between players', fn = verifyListenerCentred, retryFor = 20},
  -- retries: the bridge only pushes a key once it changes, so a just-spawned car has nothing cached
  {at = 25, what = 'hud telemetry resolves',              fn = verifyHudData, retryFor = 25},
  {at = 26, what = 'vehicle picker opens and scrolls',    fn = verifyPicker, retryFor = 10},
  {at = 27, what = 'repeat spawn does not add cars',      fn = verifyNoOverSpawn, retryFor = 20},
  {at = 29, what = 'menu opens and closes',               fn = verifyMenu, retryFor = 5},
  {at = 30, what = 'vehicle thumbnails resolve',          fn = verifyThumbnails, retryFor = 10},
  {at = 31, what = 'schedule targeted vehicle replacement', fn = startTargetedReplacement},
  {at = 32, what = 'other player stays live during replacement', fn = verifyUnaffectedPlayerLive, retryFor = 5},
  {at = 34, what = 'targeted replacement restores changed player', fn = verifyTargetedReplacementRestored, retryFor = 45},
  {at = 35, what = 'screenshot',                     fn = function()
    if not extensions.render_splitScreen.captureScreenshot() then error('screenshot could not be scheduled') end
  end},
  -- deliberately after the screenshot, which is exactly what switches the imgui layer off
  {at = 37, what = 'overlay still visible after screenshot', fn = verifyImguiAlive, retryFor = 10},
  {at = 0, fn = summarise}, -- at=0: fires as soon as every phase before it has settled
  -- end in a normal playable state; an unattended test must not leave a modal picker open
  {at = 2, fn = function() end},
}

-- true once the phase is settled (passed, or out of retries) and the runner may advance
local function runPhase(p)
  if not p.what then p.fn(); return true end
  if not p.retryFor then try(p.what, p.fn); return true end
  local ok, err = pcall(p.fn)
  if ok then note(true, p.what); return true end
  -- A prior asynchronous phase may legitimately hold the runner past this phase's nominal `at`.
  -- Start the retry window on the first actual attempt, not at level-clock time p.at.
  if not p.deadline then p.deadline = t + p.retryFor end
  if t < p.deadline then return false end -- still inside the window; try again next frame
  note(false, p.what, tostring(err) .. string.format(' (gave up after %ds)', p.retryFor))
  return true
end

-- The clock only starts once the level has settled and player 0 is in a car -- the mod filesystem
-- mounts well before the level finishes loading, so this may idle for a while.
local started = false
local function onUpdate(dtReal)
  if not started then
    if not (getPlayerVehicle and getPlayerVehicle(0)) then return end
    started = true
    log('I', LOG, 'player vehicle present -- starting selftest clock')
  end
  t = t + dtReal
  local p = phases[phase]
  if p and t >= p.at then
    if runPhase(p) then phase = phase + 1 end
  end
end

M.onUpdate = onUpdate
return M
