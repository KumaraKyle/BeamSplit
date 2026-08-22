local M = {}

local function loadBackend()
  if extensions and not extensions.isExtensionLoaded("render_splitScreen") then
    extensions.load("render_splitScreen")
  end
end

M.onExtensionLoaded = loadBackend
M.onClientPostStartMission = loadBackend

-- modScript.lua is executed directly by BeamNG's mod manager; the table returned
-- below is not registered as an extension, so its callbacks are never invoked.
-- Load the real extension immediately while the mod manager's compatibility
-- wrapper is active. The backend then receives mission lifecycle callbacks itself.
loadBackend()

return M
