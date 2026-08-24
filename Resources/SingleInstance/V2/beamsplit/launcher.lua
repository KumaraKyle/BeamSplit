-- Launcher-only bridge. The public BeamSplit v2 mod stays manually controlled, while the
-- desktop launcher supplies a session manifest and expects the split to start after Freeroam.
local M = {}

local sessionPath = '/settings/beamsplit/session.json'
local pending = true
local settledFor = 0
local START_DELAY = 2.0

local function readSession()
  local value = jsonReadFile(sessionPath)
  if type(value) ~= 'table' or value.schemaVersion ~= 1 or type(value.players) ~= 'table' then
    return nil
  end
  return value
end

local function start()
  if not pending or not getPlayerVehicle(0) or type(beamsplit) ~= 'table' then return end
  local session = readSession()
  if not session or #session.players < 2 then
    pending = false
    log('E', 'beamsplit.launcher', 'invalid single-instance session manifest')
    return
  end

  local rects, assignments = {}, {}
  for i, player in ipairs(session.players) do
    local rect = player.rect
    if type(rect) ~= 'table' or #rect < 4 then
      pending = false
      log('E', 'beamsplit.launcher', 'player ' .. i .. ' has no valid viewport rectangle')
      return
    end
    rects[i] = {x = rect[1], y = rect[2], w = rect[3], h = rect[4]}
    assignments[i] = {
      player = tonumber(player.player) or (i - 1),
      device = player.device,
      keyboard = player.keyboard == true
    }
  end

  pending = false
  local ok, err = pcall(beamsplit.on, #session.players, {
    rects = rects,
    deviceAssignments = assignments,
    showHud = session.hud ~= false,
    audio = session.audio or 'native'
  })
  if not ok then
    log('E', 'beamsplit.launcher', 'automatic split-screen start failed: ' .. tostring(err))
    guihooks.trigger('toastrMsg', {
      type = 'error', title = 'BeamSplit unavailable',
      msg = 'The single-instance split could not start. See beamng.log for details.'
    })
  else
    log('I', 'beamsplit.launcher', 'working v2 split-screen started from launcher session')
  end
end

function M.onClientPostStartMission()
  pending = true
  settledFor = 0
end

function M.onUpdate(dtReal)
  if not pending then return end
  if not getPlayerVehicle(0) then settledFor = 0; return end
  -- onClientPostStartMission fires while BeamNG is still rebuilding vehicle bindings. Calling
  -- assignPlayerToDevice in that same frame can crash in native code, so require a short period
  -- with a live player vehicle before creating views or touching controller ownership.
  settledFor = settledFor + (dtReal or 0)
  if settledFor >= START_DELAY then start() end
end

return M
