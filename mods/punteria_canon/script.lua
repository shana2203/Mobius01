-- Punteria del canon (SLUS-20152): gun aim assist and lead sight.
--
-- 001249A0 fires the player's gun one round at a time: it copies the
-- aircraft's angles (pitch, yaw), adds the random spread and calls
-- 0014C8F0(owner, muzzle position, angles, slot velocity) to make the round
-- (ra 00124D74 / 00124D94, one per barrel).  The round flies with the
-- aircraft's velocity plus 85 units per field along
--     (sin yaw * cos pitch, -sin pitch, cos yaw * cos pitch)    (+y is down).
-- The slot table (stride 0xA0, the player's slot first, read32(002A1F70)):
-- +0x00 position, +0x20 velocity per field, +0x44 aircraft data, bit 0 of the
-- byte at +0x48 set for enemies, and +0x4C zero while the unit is not in play
-- (hidden ones waiting at a placeholder position).
--
-- Before each round, the enemy closest to the line of fire inside the cone
-- (and in range) is led: where it will be when the round gets there, from
-- both velocities; the round is turned toward that point (by "fuerza").
--
-- The lead sight ("mira"): 001029E0 builds each camera's world -> screen
-- matrix at camera+0xE0 (sceVu0ViewScreenMatrix * view; screen centre at
-- +0x178/+0x17C, 2048,2048).  Each field the sight marks where the nose has
-- to point for the rounds to meet the enemy nearest the line of fire.
local function num(key, default, lo, hi)
    return math.max(lo, math.min(hi, tonumber(ac4.config(key)) or default))
end
local function flag(key, default)
    local v = ac4.config(key)
    if v == nil then return default end
    v = tostring(v):lower()
    return v == "true" or v == "1" or v == "si" or v == "yes"
end

local cone = math.rad(num("cono_grados", 5, 0.5, 30))
local reach = num("alcance", 4500, 500, 30000)
local strength = num("fuerza", 1, 0, 1)
local assist = flag("ayuda", true)
local sight = flag("mira", true)
local sight_cone = math.rad(num("mira_cono_grados", 25, 2, 60))
local sight_size = num("mira_tamano", 9, 4, 30)
local debug = flag("mira_depurar", false)
local MUZZLE = 85.0
local STRIDE, MAX_SLOTS = 0xA0, 256
local cos_cone = math.cos(cone)
local cos_sight = math.cos(sight_cone)
local GREEN, AMBER = 0x40FF60, 0xFFC020

local function f(a) return ac4.read_float(a) end
local function valid_ptr(p) return p >= 0x100000 and p < 0x01FFF000 end

-- the lead of one target: unit direction the round must leave along and the
-- time it takes (in fields), or nil.  r = target - muzzle, w = relative velocity
local function lead(rx, ry, rz, wx, wy, wz)
    local dist2 = rx * rx + ry * ry + rz * rz
    local a = wx * wx + wy * wy + wz * wz - MUZZLE * MUZZLE
    local b = 2 * (rx * wx + ry * wy + rz * wz)
    local t
    if math.abs(a) < 1e-6 then
        t = b ~= 0 and -dist2 / b or nil
    else
        local disc = b * b - 4 * a * dist2
        if disc >= 0 then
            local q = math.sqrt(disc)
            local t1, t2 = (-b - q) / (2 * a), (-b + q) / (2 * a)
            t = (t1 > 0 and (t2 <= 0 or t1 < t2)) and t1 or (t2 > 0 and t2 or nil)
        end
    end
    if not t or t <= 0 then return nil end
    local lx, ly, lz = rx + wx * t, ry + wy * t, rz + wz * t
    local l = math.sqrt(lx * lx + ly * ly + lz * lz)
    if l < 1e-3 then return nil end
    return lx / l, ly / l, lz / l, t
end

-- the enemies, refreshed once per field
local enemies, count = {}, 0
local function refresh()
    count = 0
    local me = ac4.read32(0x002A1F70)
    if not valid_ptr(me) then return end
    local misses = 0
    for i = 1, MAX_SLOTS - 1 do
        local s = me + i * STRIDE
        local air = ac4.read32(s + 0x44)
        if valid_ptr(air) then
            misses = 0
            if (ac4.read8(s + 0x48) & 1) ~= 0 and ac4.read32(s + 0x4C) ~= 0 then
                local x = f(s)
                if x == x and math.abs(x) < 1e7 then
                    count = count + 1
                    enemies[count] = s
                end
            end
        else
            misses = misses + 1
            if misses > 16 then break end
        end
    end
end

-- the enemy whose lead lies closest to direction d0, within cos_limit and
-- reach: its lead direction and how close (cosine), or nil
local function best_lead(mx, my, mz, vx, vy, vz, dx0, dy0, dz0, cos_limit)
    local best, bx, by, bz, bs
    for k = 1, count do
        local s = enemies[k]
        local rx, ry, rz = f(s) - mx, f(s + 4) - my, f(s + 8) - mz
        local dist = math.sqrt(rx * rx + ry * ry + rz * rz)
        if dist > 1 and dist <= reach and (rx * dx0 + ry * dy0 + rz * dz0) / dist >= cos_limit then
            local lx, ly, lz = lead(rx, ry, rz, f(s + 0x20) - vx, f(s + 0x24) - vy, f(s + 0x28) - vz)
            if lx then
                local c2 = lx * dx0 + ly * dy0 + lz * dz0     -- how far off the line of fire
                if c2 >= cos_limit and (not best or c2 > best) then
                    best, bx, by, bz, bs = c2, lx, ly, lz, s
                end
            end
        end
    end
    return best, bx, by, bz, bs
end

if assist then
    local hook = ac4.hook_before(0x0014C8F0, function(ctx)
        local ra = ac4.reg(ctx, 31)
        if ra ~= 0x00124D74 and ra ~= 0x00124D94 then return end
        if count == 0 or strength == 0 then return end
        local me = ac4.read32(0x002A1F70)
        local vel = ac4.arg(ctx, 3)
        if vel ~= me + 0x20 then return end            -- the player's rounds only
        local muzzle, ang = ac4.arg(ctx, 1), ac4.arg(ctx, 2)
        local pitch, yaw = f(ang), f(ang + 4)
        if pitch ~= pitch or yaw ~= yaw then return end
        local cp = math.cos(pitch)
        local dx0, dy0, dz0 = math.sin(yaw) * cp, -math.sin(pitch), math.cos(yaw) * cp
        local best, bx, by, bz = best_lead(f(muzzle), f(muzzle + 4), f(muzzle + 8),
            f(vel), f(vel + 4), f(vel + 8), dx0, dy0, dz0, cos_cone)
        if not best then return end
        local nx = dx0 + (bx - dx0) * strength
        local ny = dy0 + (by - dy0) * strength
        local nz = dz0 + (bz - dz0) * strength
        local l = math.sqrt(nx * nx + ny * ny + nz * nz)
        nx, ny, nz = nx / l, ny / l, nz / l
        ac4.write_float(ang, -math.asin(math.max(-1, math.min(1, ny))))
        ac4.write_float(ang + 4, math.atan(nx, nz))
    end)
    if not hook then error("No se pudo registrar la punteria del canon") end
end

-- cameras built this field (001029E0); the main view is the one with the
-- widest viewport, the last of them if several are as wide
local cams, cur = {}, nil
if sight then
    ac4.hook_before(0x001029E0, function(ctx) cur = ac4.arg(ctx, 0) end)
    ac4.hook_after(0x001029E0, function()
        if cur then cams[#cams + 1] = cur end
        cur = nil
    end)
end

local function main_camera()
    local best, area = nil, -1
    for _, c in ipairs(cams) do
        local w, h = ac4.read16(c + 0x18C), ac4.read16(c + 0x18E)
        if w < 0x8000 and h < 0x8000 and w * h >= area then best, area = c, w * h end
    end
    return best, area
end

local function project(c, x, y, z)
    local m = c + 0xE0
    local X = f(m) * x + f(m + 16) * y + f(m + 32) * z + f(m + 48)
    local Y = f(m + 4) * x + f(m + 20) * y + f(m + 36) * z + f(m + 52)
    local W = f(m + 12) * x + f(m + 28) * y + f(m + 44) * z + f(m + 60)
    if W <= 1 then return nil end
    return X / W - f(c + 0x178), Y / W - f(c + 0x17C)
end

local n = 0
ac4.on_field(function()
    n = n + 1
    refresh()
    if not sight then return end
    local cam, area = main_camera()
    cams = {}
    if not cam or area < 200 * 150 or count == 0 then return end
    local me = ac4.read32(0x002A1F70)
    if not valid_ptr(me) then return end
    -- the nose: the aircraft's pitch and yaw at +0x10 (same convention as
    -- the gun's angles)
    local pitch, yaw = f(me + 0x10), f(me + 0x14)
    if pitch ~= pitch or yaw ~= yaw then return end
    local cp = math.cos(pitch)
    local dx0, dy0, dz0 = math.sin(yaw) * cp, -math.sin(pitch), math.cos(yaw) * cp
    local mx, my, mz = f(me), f(me + 4), f(me + 8)
    -- only in flight: the view's camera rides with the player's aircraft
    -- (cockpit, HUD or chase view), not the hangar's, a menu's or a replay's
    local cdx, cdy, cdz = f(cam) - mx, f(cam + 4) - my, f(cam + 8) - mz
    local cam_dist = math.sqrt(cdx * cdx + cdy * cdy + cdz * cdz)
    if debug and n % 30 == 0 then ac4.log(string.format("mira: camara a %.0f del avion", cam_dist)) end
    if not (cam_dist < 1500) or ac4.read32(me + 0x4C) == 0 then return end
    local best, bx, by, bz, s = best_lead(mx, my, mz, f(me + 0x20), f(me + 0x24), f(me + 0x28),
        dx0, dy0, dz0, cos_sight)
    if debug and n % 30 == 0 then
        local vx, vy, vz = f(me + 0x20), f(me + 0x24), f(me + 0x28)
        local vl = math.sqrt(vx * vx + vy * vy + vz * vz)
        ac4.log(string.format("mira: campo %d cam %08X (%dx%d @%.0f,%.0f) nariz(%.3f %.3f %.3f) vel(%.3f %.3f %.3f) enemigos %d %s",
            n, cam, ac4.read16(cam + 0x18C), ac4.read16(cam + 0x18E), f(cam + 0x178), f(cam + 0x17C),
            dx0, dy0, dz0, vx / math.max(vl, 1e-3), vy / math.max(vl, 1e-3), vz / math.max(vl, 1e-3),
            count, best and string.format("blanco %08X", s) or "sin blanco"))
        for k = 1, count do
            local e = enemies[k]
            local rx, ry, rz = f(e) - mx, f(e + 4) - my, f(e + 8) - mz
            local d = math.sqrt(rx * rx + ry * ry + rz * rz)
            local c = (rx * dx0 + ry * dy0 + rz * dz0) / math.max(d, 1e-3)
            if math.deg(math.acos(math.max(-1, math.min(1, c)))) < 40 then
                ac4.log(string.format("mira:   %08X dist %.0f angulo %.1f vel(%.1f %.1f %.1f)", e, d,
                    math.deg(math.acos(math.max(-1, math.min(1, c)))), f(e + 0x20), f(e + 0x24), f(e + 0x28)))
            end
        end
    end
    if debug then
        local x, y = project(cam, mx + dx0 * 3000, my + dy0 * 3000, mz + dz0 * 3000)
        if x then ac4.draw_line(x - 6, y, x + 6, y, 0xFF40FF) ac4.draw_line(x, y - 6, x, y + 6, 0xFF40FF) end
        for k = 1, count do
            local e = enemies[k]
            local ex, ey = project(cam, f(e), f(e + 4), f(e + 8))
            if ex then ac4.draw_circle(ex, ey, 4, 0xFF40FF, 1, 1) end
        end
    end
    if not best then return end
    local rx, ry, rz = f(s) - mx, f(s + 4) - my, f(s + 8) - mz
    local dist = math.sqrt(rx * rx + ry * ry + rz * rz)
    local x, y = project(cam, mx + bx * dist, my + by * dist, mz + bz * dist)
    if not x or math.abs(x) > 400 or math.abs(y) > 300 then return end
    local color = (assist and best >= cos_cone) and GREEN or AMBER
    ac4.draw_circle(x, y, sight_size, color, 0.9, 1.5)
    ac4.draw_disc(x, y, 1.2, color, 0.9)
end)

ac4.log(string.format("punteria_canon: ayuda %s (cono %.1f grados, alcance %.0f, fuerza %.0f%%), mira %s",
    assist and "si" or "no", math.deg(cone), reach, strength * 100, sight and "si" or "no"))
