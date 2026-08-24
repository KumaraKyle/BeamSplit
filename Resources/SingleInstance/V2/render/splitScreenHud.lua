-- BeamSplit v2 -- per-tile HUD ("Rail").
--
-- The stock HUD is CEF/Ultralight: one browser view, full window, driven by player 0. It cannot be
-- instanced per tile, so each tile gets this instead, drawn into the compositor's imgui overlay.
--
-- Design: one horizontal rail pinned bottom-centre, everything on a single baseline so the eye
-- never hunts across the tile. Reading order left to right is identity -> speed -> gear/rpm ->
-- consumables, which is roughly the order they matter while driving. RPM is 20 discrete segments
-- rather than a continuous bar: segments read like shift lights in peripheral vision, where a
-- smooth fill does not. The player accent is the rail's left spine -- structural, so a tile is
-- identifiable from the shape alone, not a decorative stripe.
--
-- Legibility: the rail is a dark translucent slab with a light hairline border, which holds up over
-- both bright sky and dark tarmac. Hierarchy comes from SIZE and COLOUR, never weight -- imgui's
-- default font has one weight, so a design leaning on bold/regular contrast would flatten out here.
--
-- Telemetry:
--   speed/rpm/gear/fuel  core_vehicleBridge -- registerValueChangeNotification pushes an electrics
--                        key GE-side on change, getCachedVehicleData reads the last value. Speed
--                        falls back to rigid body velocity, because a change notification never
--                        fires for a value that has not changed and a parked car would read blank.
--   damage               map.objects[vid].damage, which map.lua maintains GE-side.
--   fuel range           not published by the game at all -- derived from litres burned per km
--                        actually driven (see updateTrip).
--
-- Drawing uses ImDrawList_AddRectFilled/AddRect/AddText1 plus SetWindowFontScale + im.Text for the
-- scaled readouts. ImDrawList_AddText2 (the variant taking an explicit font and size) is unused
-- anywhere in the shipped game and throws through the FFI wrapper on a nil font -- which, inside
-- the compositor's Begin/End pair, imgui reports as "Missing End()", taking down the whole overlay
-- rather than just this widget.

local M = {}
local im = ui_imgui

local KEYS = {'wheelspeed', 'rpm', 'gear', 'gearIndex', 'gear_A', 'fuel', 'fuelVolume', 'fuelCapacity'}
local subscribed = {}
local trip = {}

-- ---- design tokens, in rail units (scaled per tile) -------------------------
local RAIL_H     = 88
local SPINE_W    = 44
local PAD        = 20
local DIV_INSET  = 16
local SEG_COUNT  = 20
local SEG_W, SEG_GAP, SEG_H = 11, 3, 16
local SEG_RED    = 3      -- last N segments are the redline block
local GEAR_W     = 46
local METER_W    = 190    -- fuel / damage block
local BOTTOM     = 26     -- gap from the tile's bottom edge

local RPM_MAX = 8000
-- the game's own severity steps for accumulated damage (gameplay/freeformDelivery/debug.lua
-- treats >1000 as moderate and >5000 as severe)
local DAMAGE_WARN, DAMAGE_BAD = 1000, 5000

function M.subscribe(veh)
  if not veh then return end
  local vid = veh:getID()
  if subscribed[vid] then return end
  for _, key in ipairs(KEYS) do
    core_vehicleBridge.registerValueChangeNotification(veh, key)
  end
  subscribed[vid] = true
end

function M.forget(vid) subscribed[vid] = nil; trip[vid] = nil end
function M.reset() subscribed = {}; trip = {} end

local function u32(r, g, b, a) return im.GetColorU322(im.ImVec4(r, g, b, a or 1)) end

local PANEL     = u32(13 / 255, 15 / 255, 17 / 255, 0.72)
local HAIRLINE  = u32(1, 1, 1, 0.14)
local INK       = u32(244 / 255, 246 / 255, 247 / 255, 1)
local INK_60    = u32(244 / 255, 246 / 255, 247 / 255, 0.6)
local INK_50    = u32(244 / 255, 246 / 255, 247 / 255, 0.5)
local INK_45    = u32(244 / 255, 246 / 255, 247 / 255, 0.45)
local INK_85    = u32(244 / 255, 246 / 255, 247 / 255, 0.85)
local TRACK     = u32(1, 1, 1, 0.16)
local REDLINE   = u32(228 / 255, 0, 0, 1)
local REDLINE_D = u32(228 / 255, 0, 0, 0.28)
local FUEL      = u32(92 / 255, 200 / 255, 255 / 255, 1)
local DMG_OK    = u32(78 / 255, 201 / 255, 106 / 255, 1)
local DMG_WARN  = u32(1, 165 / 255, 15 / 255, 1)
local WHITE     = u32(1, 1, 1, 1)

-- ---- gear label ------------------------------------------------------------
-- Follows the stock SimpleGears fallback chain. The shift logic normally publishes `gear` as a
-- ready-to-display string ("N", "R", "D", "3"), which is what the in-car gauges use
-- (controller/etkGauges.lua). Some paths leave it numeric -- backwardsCompatibility.lua seeds
-- gear_A = 0 -- which is where a bare "0" comes from.
local AUTO_GEARS = {'P', 'R', 'N', 'D', '2', '1'}

local function gearLabel(gear, gearIndex, gearA)
  if type(gear) == 'string' and gear ~= '' then return gear end
  local idx = tonumber(gearIndex)
  if idx then
    if idx == 0 then return 'N' end
    if idx < 0 then return 'R' end
    return tostring(idx)
  end
  local a = tonumber(gearA)
  if a then return AUTO_GEARS[math.floor(a * 5 + 0.5) + 1] or 'N' end
  local g = tonumber(gear)
  if g then
    if g == 0 then return 'N' end
    if g < 0 then return 'R' end
    return tostring(g)
  end
  return '-'
end

-- ---- fuel range ------------------------------------------------------------
-- Range needs litres-per-km, which nothing publishes, so measure it: accumulate distance driven
-- and litres burned, folding each finished 200 m segment into a smoothed average. Segments stop
-- the estimate swinging on a single throttle stab, and it survives refuelling because a *rise* in
-- fuel contributes no consumption.
local SEGMENT_M = 200
local function updateTrip(vid, veh, litres)
  local t = trip[vid]
  local pos = veh:getPosition()
  if not t then
    trip[vid] = {pos = vec3(pos), litres = litres, dist = 0, used = 0, lpk = nil}
    return nil
  end
  if not litres then return t.lpk end
  local step = (pos - t.pos):length()
  t.pos:set(pos)
  if step > 0.05 and step < 200 then t.dist = t.dist + step end -- ignore teleports/resets
  -- t.litres starts nil when the first frame ran before the bridge pushed fuelVolume, so there is
  -- no previous reading to difference against until the second sample
  if t.litres then
    local burned = t.litres - litres
    if burned > 0 then t.used = t.used + burned end
  end
  t.litres = litres
  if t.dist >= SEGMENT_M then
    local lpk = t.used / (t.dist / 1000)
    t.lpk = t.lpk and (t.lpk * 0.7 + lpk * 0.3) or lpk
    t.dist, t.used = 0, 0
  end
  return t.lpk
end

local function sample(vid, veh)
  local get = core_vehicleBridge.getCachedVehicleData
  local litres = get(vid, 'fuelVolume')
  local lpk = updateTrip(vid, veh, litres)
  local mapObj = map and map.objects and map.objects[vid]
  return {
    speed = math.abs(get(vid, 'wheelspeed') or veh:getVelocity():length()) * 3.6,
    rpm = get(vid, 'rpm') or 0,
    gear = gearLabel(get(vid, 'gear'), get(vid, 'gearIndex'), get(vid, 'gear_A')),
    fuel = get(vid, 'fuel'),
    litres = litres,
    capacity = get(vid, 'fuelCapacity'),
    damage = (mapObj and mapObj.damage) or 0,
    range = (lpk and lpk > 0.01 and litres) and (litres / lpk) or nil,
  }
end

-- ---- text ------------------------------------------------------------------
-- Widths are estimated rather than measured: CalcTextSize reports at the ambient font scale, which
-- fights SetWindowFontScale. Digits in a sans face run ~0.54 em, which is close enough to place
-- right- and centre-aligned readouts on a rail this size.
local EM = 0.54

local function textWidth(str, sizePx) return #str * sizePx * EM end

-- align: 'l' (default), 'r', 'c'. y is the TOP of the text.
local function txt(x, y, sizePx, col, str, align)
  if align == 'r' then x = x - textWidth(str, sizePx)
  elseif align == 'c' then x = x - textWidth(str, sizePx) * 0.5 end
  im.SetWindowFontScale(sizePx / math.max(1, im.GetFontSize()))
  -- HUD geometry is in desktop/screen coordinates so it can share the compositor draw list even
  -- when BeamNG's main viewport lives on a non-primary monitor.
  im.SetCursorScreenPos(im.ImVec2(x, y))
  im.PushStyleColor1(im.Col_Text, col) -- ...1 takes ImU32, ...2 takes ImVec4; u32() gives ImU32
  im.TextUnformatted(str)
  im.PopStyleColor(1)
  im.SetWindowFontScale(1)
end

local function rect(dl, x0, y0, x1, y1, col, rounding)
  im.ImDrawList_AddRectFilled(dl, im.ImVec2(x0, y0), im.ImVec2(x1, y1), col, rounding or 0, 0)
end

-- a bar with a rounded track and a proportional fill
local function meter(dl, x, y, w, h, frac, col)
  rect(dl, x, y, x + w, y + h, TRACK, h * 0.5)
  frac = math.max(0, math.min(1, frac or 0))
  if frac > 0 then rect(dl, x, y, x + w * frac, y + h, col, h * 0.5) end
end

-- ---- the rail --------------------------------------------------------------

-- Laid out left to right against a running cursor, so blocks can size to their content and the
-- whole rail is centred from the measured total rather than a guessed constant.
local function railWidth(s, speedStr)
  local speedW = PAD + textWidth(speedStr, 58 * s) + 8 * s + textWidth('KM/H', 12 * s) + 22 * s
  local rpmW = SEG_COUNT * (SEG_W + SEG_GAP) * s - SEG_GAP * s
  local gearW = PAD * s + GEAR_W * s + 16 * s + rpmW + PAD * s
  return SPINE_W * s + speedW + 1 + gearW + 1 + (18 * s + METER_W * s + PAD * s)
end

-- `rect` is the tile in window pixels; `color` is the player's tag colour ({r,g,b})
function M.draw(dl, tile, player, color, veh)
  -- One scale for the whole rail. The divisors are tuned so the rail lands at roughly 60-75% of
  -- the tile's width across every layout: a 1920x539 stacked tile is width-rich but height-poor,
  -- a 960x1080 side-by-side tile is the reverse, so both dimensions get a say and the smaller
  -- wins. Scaling off the tile's smaller dimension alone left the rail at ~36% of a stacked tile,
  -- which shrank the 9px labels below couch-legible.
  local s = math.max(0.7, math.min(1.6, math.min(tile.w / 1100, tile.h / 420)))
  local accent = u32(color[1], color[2], color[3], 1)

  local vid = veh and veh:getID()
  local d = vid and sample(vid, veh) or nil
  local speedStr = d and string.format('%.0f', d.speed) or '--'

  local total = railWidth(s, speedStr)
  local h = RAIL_H * s
  local x = tile.x + (tile.w - total) * 0.5
  local y = tile.y + tile.h - BOTTOM * s - h
  local r = 8 * s

  -- slab + hairline: dark enough to hold text over bright sky, translucent enough not to blind
  rect(dl, x, y, x + total, y + h, PANEL, r)
  im.ImDrawList_AddRect(dl, im.ImVec2(x, y), im.ImVec2(x + total, y + h), HAIRLINE, r, 0, 1)

  -- accent spine: rounded like the slab, then squared off on its right edge so it reads as part
  -- of the rail rather than a pill sitting on it
  local spineW = SPINE_W * s
  rect(dl, x, y, x + spineW, y + h, accent, r)
  rect(dl, x + spineW - r, y, x + spineW, y + h, accent, 0)
  txt(x + spineW * 0.5, y + h * 0.28, 22 * s, WHITE, tostring(player + 1), 'c')
  txt(x + spineW * 0.5, y + h * 0.62, 9 * s, u32(1, 1, 1, 0.85), 'PLR', 'c')

  local cx = x + spineW

  if not d then
    txt(cx + PAD * s, y + h * 0.4, 14 * s, INK_60, 'no car')
    return
  end

  -- speed: the one value readable from a couch, so it gets the largest type on the rail
  cx = cx + PAD * s
  txt(cx, y + h * 0.22, 58 * s, INK, speedStr)
  cx = cx + textWidth(speedStr, 58 * s) + 8 * s
  txt(cx, y + h * 0.56, 12 * s, INK_60, 'KM/H')
  cx = cx + textWidth('KM/H', 12 * s) + 22 * s

  rect(dl, cx, y + DIV_INSET * s, cx + 1, y + h - DIV_INSET * s, HAIRLINE)
  cx = cx + 1 + PAD * s

  -- gear
  txt(cx + GEAR_W * s * 0.5, y + h * 0.24, 30 * s, INK, d.gear, 'c')
  txt(cx + GEAR_W * s * 0.5, y + h * 0.66, 10 * s, INK_50, 'GEAR', 'c')
  cx = cx + GEAR_W * s + 16 * s

  -- rpm as discrete segments; the redline block stays visible unlit so you can see it coming
  local lit = math.floor((math.min(d.rpm, RPM_MAX) / RPM_MAX) * SEG_COUNT + 0.5)
  local segY = y + h * 0.26
  local rpmW = SEG_COUNT * (SEG_W + SEG_GAP) * s - SEG_GAP * s
  for i = 0, SEG_COUNT - 1 do
    local red = i >= SEG_COUNT - SEG_RED
    local col = (i < lit) and (red and REDLINE or INK) or (red and REDLINE_D or TRACK)
    local sx = cx + i * (SEG_W + SEG_GAP) * s
    rect(dl, sx, segY, sx + SEG_W * s, segY + SEG_H * s, col, 1 * s)
  end
  txt(cx, segY + (SEG_H + 6) * s, 10 * s, INK_45, 'RPM')
  txt(cx + rpmW, segY + (SEG_H + 6) * s, 10 * s, INK_45, string.format('%.0f', d.rpm), 'r')
  cx = cx + rpmW + PAD * s

  rect(dl, cx, y + DIV_INSET * s, cx + 1, y + h - DIV_INSET * s, HAIRLINE)
  cx = cx + 1 + 18 * s

  -- fuel and damage share a row shape, so the second reads as a variant of the first
  local mw = METER_W * s
  local labelW, valueW = 30 * s, 74 * s
  local barW = mw - labelW - valueW - 18 * s
  local rowY = y + h * 0.30

  txt(cx, rowY, 10 * s, INK_50, 'FUEL')
  meter(dl, cx + labelW, rowY + 2 * s, barW, 5 * s, d.fuel or 0, FUEL)
  local fuelText = d.litres and string.format('%.0f L', d.litres) or '--'
  if d.range then fuelText = fuelText .. string.format('  %.0f km', d.range) end
  txt(cx + mw, rowY - 1 * s, 11 * s, INK_85, fuelText, 'r')

  rowY = y + h * 0.58
  local dcol = DMG_OK
  if d.damage > DAMAGE_BAD then dcol = REDLINE elseif d.damage > DAMAGE_WARN then dcol = DMG_WARN end
  txt(cx, rowY, 10 * s, INK_50, 'DMG')
  meter(dl, cx + labelW, rowY + 2 * s, barW, 5 * s, d.damage / DAMAGE_BAD, dcol)
  txt(cx + mw, rowY - 1 * s, 11 * s, INK_85,
    d.damage > 0 and string.format('%.0f', d.damage) or 'OK', 'r')
end

return M

