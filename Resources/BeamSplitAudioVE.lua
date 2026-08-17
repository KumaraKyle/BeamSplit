-- BeamSplit per-listener BeamMP audio isolation.
-- BeamMP sets v.mpVehicleType to "L" for this client's car and "R" for remote cars.
-- This extension is loaded by BeamMP's existing vehicle-extension loader.

local M = {}
local muted = false
local originalSoundsUpdateGFX = nil
local originalPlayAtNode = nil
local originalPlayFollowNode = nil
local originalBodyCollision = nil
local originalEngineUpdates = {}
local originalWheelCoefs = {}
local nop = function() end

local function muteRemoteVehicle()
  if muted then return end

  -- Stop/disable tire, wind, beam, scrape and impact emitters for this remote copy.
  if sounds then
    if sounds.reset then sounds.reset() end
    originalSoundsUpdateGFX = sounds.updateGFX
    originalPlayAtNode = sounds.playSoundOnceAtNode
    originalPlayFollowNode = sounds.playSoundOnceFollowNode
    originalBodyCollision = sounds.bodyCollision
    sounds.updateGFX = nop
    sounds.playSoundOnceAtNode = nop
    sounds.playSoundOnceFollowNode = nop
    sounds.bodyCollision = nop
  end

  if wheels and wheels.wheels then
    for id, wheel in pairs(wheels.wheels) do
      originalWheelCoefs[id] = wheel.tireSoundVolumeCoef
      wheel.tireSoundVolumeCoef = 0
    end
  end

  -- Engine/exhaust audio is mixed from powertrain.updateSounds after extension hooks.
  -- Wrap that callback so the stock mixer sees a zero coefficient only for this frame.
  if powertrain and powertrain.getDevicesByCategory then
    for _, engine in pairs(powertrain.getDevicesByCategory("engine") or {}) do
      if engine.updateSounds and not originalEngineUpdates[engine] then
        local original = engine.updateSounds
        originalEngineUpdates[engine] = original
        engine.updateSounds = function(device, dt)
          local oldCoef = device.engineVolumeCoef
          device.engineVolumeCoef = 0
          original(device, dt)
          device.engineVolumeCoef = oldCoef
        end
      end
    end
  end

  muted = true
  log('I', 'BeamSplitAudioVE', 'remote vehicle audio suppressed for vehicle '..tostring(obj:getID()))
end

local function restoreLocalVehicle()
  if not muted then return end

  if sounds and originalSoundsUpdateGFX then
    sounds.updateGFX = originalSoundsUpdateGFX
    sounds.playSoundOnceAtNode = originalPlayAtNode
    sounds.playSoundOnceFollowNode = originalPlayFollowNode
    sounds.bodyCollision = originalBodyCollision
    if sounds.reset then sounds.reset() end
  end

  if wheels and wheels.wheels then
    for id, coef in pairs(originalWheelCoefs) do
      if wheels.wheels[id] then wheels.wheels[id].tireSoundVolumeCoef = coef end
    end
  end

  for engine, original in pairs(originalEngineUpdates) do
    engine.updateSounds = original
  end

  originalSoundsUpdateGFX = nil
  originalPlayAtNode = nil
  originalPlayFollowNode = nil
  originalBodyCollision = nil
  originalEngineUpdates = {}
  originalWheelCoefs = {}
  muted = false
end

local function updateGFX(dt)
  if v.mpVehicleType == "R" then
    muteRemoteVehicle()
  else
    restoreLocalVehicle()
  end
end

local function onExtensionUnloaded()
  restoreLocalVehicle()
end

local function onExtensionLoaded()
  log('I', 'BeamSplitAudioVE', 'per-listener audio hook loaded for vehicle '..tostring(obj:getID()))
end

M.updateGFX = updateGFX
M.onExtensionLoaded = onExtensionLoaded
M.onExtensionUnloaded = onExtensionUnloaded

return M
