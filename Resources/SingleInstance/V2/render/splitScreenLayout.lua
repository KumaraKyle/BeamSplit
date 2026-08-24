-- BeamSplit v2 -- tile layout maths for split-screen.
-- Pure geometry: no engine state, so it can be unit-tested standalone.

local M = {}

-- rect helper, in window pixels, origin top-left
local function rect(x, y, w, h)
  return {x = math.floor(x), y = math.floor(y), w = math.floor(w), h = math.floor(h)}
end

-- Split [start, start+total) into `n` bands separated by `gutter` px.
local function bands(start, total, n, gutter)
  local inner = total - gutter * (n - 1)
  local size = inner / n
  local res = {}
  for i = 1, n do
    res[i] = {pos = start + (i - 1) * (size + gutter), size = size}
  end
  return res
end

-- rows: n stacked bands, full width (classic racing split for 2 players)
local function rows(n, w, h, gutter)
  local out = {}
  for i, b in ipairs(bands(0, h, n, gutter)) do
    out[i] = rect(0, b.pos, w, b.size)
  end
  return out
end

-- columns: n side-by-side bands, full height
local function columns(n, w, h, gutter)
  local out = {}
  for i, b in ipairs(bands(0, w, n, gutter)) do
    out[i] = rect(b.pos, 0, b.size, h)
  end
  return out
end

-- 2x2 quad; with 3 players the third tile spans the whole bottom row
local function grid(n, w, h, gutter)
  local r = bands(0, h, 2, gutter)
  local c = bands(0, w, 2, gutter)
  if n == 3 then
    return {
      rect(c[1].pos, r[1].pos, c[1].size, r[1].size),
      rect(c[2].pos, r[1].pos, c[2].size, r[1].size),
      rect(0,        r[2].pos, w,         r[2].size),
    }
  end
  local out = {}
  for i = 1, n do
    local ri = math.floor((i - 1) / 2) + 1
    local ci = ((i - 1) % 2) + 1
    out[i] = rect(c[ci].pos, r[ri].pos, c[ci].size, r[ri].size)
  end
  return out
end

M.modes = {'auto', 'rows', 'columns', 'grid'}

-- Tile rects for `n` players inside a `w` x `h` window.
--   mode: 'auto' (rows for 2, grid for 3-4), 'rows', 'columns', 'grid'
--   gutter: pixels of black between tiles
function M.compute(n, w, h, mode, gutter)
  n = math.max(1, math.min(4, n or 1))
  gutter = gutter or 0
  if n == 1 then return {rect(0, 0, w, h)} end
  if mode == nil or mode == 'auto' then
    mode = (n == 2) and 'rows' or 'grid'
  end
  if mode == 'rows' then return rows(n, w, h, gutter) end
  if mode == 'columns' then return columns(n, w, h, gutter) end
  return grid(n, w, h, gutter)
end

return M

