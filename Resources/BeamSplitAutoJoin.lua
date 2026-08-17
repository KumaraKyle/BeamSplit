-- BeamSplit local BeamMP auto-join.
-- Loaded inside the per-instance BeamMP client only; the upstream install is untouched.

local M = {}
local serverPort = __BEAMSPLIT_SERVER_PORT__
local elapsed = 0
local pollAt = 0
local lastLoginAt = -100
local lastConnectAt = -100

local function tryJoin()
  if not MPCoreNetwork then return end
  if MPCoreNetwork.isMPSession() or MPCoreNetwork.isGoingMPSession() then
    return
  end
  if not MPCoreNetwork.isLauncherConnected() then return end

  if not MPCoreNetwork.isLoggedIn() then
    if elapsed - lastLoginAt >= 4 then
      lastLoginAt = elapsed
      log('I', 'BeamSplitAutoJoin', 'Requesting BeamMP guest login')
      MPCoreNetwork.login()
    end
    return
  end

  if elapsed - lastConnectAt < 10 then return end
  lastConnectAt = elapsed
  log('I', 'BeamSplitAutoJoin', 'Direct connecting to 127.0.0.1:' .. tostring(serverPort))
  MPCoreNetwork.connectToServer('127.0.0.1', serverPort, 'BeamSplit local', true)
end

local function onUpdate(dt)
  elapsed = elapsed + dt
  if elapsed < pollAt then return end
  pollAt = elapsed + 0.25
  tryJoin()
end

local function onLauncherConnected()
  tryJoin()
end

M.onUpdate = onUpdate
M.onLauncherConnected = onLauncherConnected
M.onExtensionLoaded = function()
  log('I', 'BeamSplitAutoJoin', 'Armed for local server port ' .. tostring(serverPort))
end
M.onInit = function() setExtensionUnloadMode(M, 'manual') end

return M
