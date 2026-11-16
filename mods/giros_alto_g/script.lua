-- AC04 NTSC-U: High-G turn while L1 + R1 are held, in the manner of Ace Combat 7:
-- the aircraft turns much tighter, nose and flight path together, and bleeds
-- speed while it does.  Version 3, built on the stock flight model.
--
-- The stock flight step (0x129500, once a field for the player) works like this
-- (obj = a0, state = a1, data = a2 of the pitch helpers 0x1282B8 / 0x128510):
--   obj+0x20..0x28   velocity (units per field)
--   state+0x2E8      angle of attack: the velocity's vertical share in body axes
--   state+0x138      pitch rate, eased towards a target set by the stick
--   state+0x000      nose pitch this field = rate - data[0x8C] * AoA
--                    (the 0x8C term is weathervane stability)
--   data[0x88]       lift: after the helpers the step turns the velocity towards
--                    the nose by data[0x88] * AoA radians a field
-- In a steady pull the nose and the path turn together (stock: AoA ~0.28,
-- ~33 deg/s).  Scaling only the nose (version 2) let the nose run ahead of the
-- path, the AoA climbed to ~0.47 and the turn felt loose.  Scaling the nose
-- AND the lift by the same factor keeps the stock balance -- the same AoA,
-- the same response shape -- k times faster: tight and precise.
--
-- Nothing is left changed: data[0x88] is set for the player's step only and
-- restored as the step ends (0x128E80), so other aircraft keep the stock data;
-- data[0x78/0x7C/0x8C] (the maniobrabilidad mod) are not touched.
-- The factor k follows L1 + R1 through a critically damped spring: it starts
-- and settles without a kink, so neither the entry nor the exit jerks.
local function num(key, default, lo, hi)
    local v = tonumber(ac4.config(key)) or default
    return math.max(lo, math.min(hi, v))
end
local GAIN    = num("giro", 1.8, 1.0, 2.5)               -- turn rate at full High-G
local FOLLOW  = num("seguimiento", 1.0, 0.0, 1.0)        -- 1: path follows the nose fully
local T_IN    = num("entrada", 0.30, 0.05, 1.0)          -- seconds to (nearly) full High-G
local T_OUT   = num("salida", 0.40, 0.05, 1.5)           -- seconds back to normal
local BLEED   = num("perdida_velocidad", 0.12, 0.0, 0.8) -- speed lost per second, full pull
local FLOOR   = num("velocidad_minima", 0.60, 0.2, 1.0)  -- never below this share of entry speed
if GAIN == 1 and BLEED == 0 then return end

local BUTTONS = ac4.BTN.l1 | ac4.BTN.r1
local DT = 1 / 59.94
local AOA_FULL = 0.28              -- the stock AoA in a full-stick pull

-- tests: prueba = "a-b" holds the buttons for flight steps a..b; registro = n logs
local force_a, force_b = (ac4.config("prueba") or ""):match("^(%d+)%-(%d+)$")
force_a, force_b = tonumber(force_a), tonumber(force_b)
local log_left = tonumber(ac4.config("registro")) or 0

local k, kv = 1.0, 0.0             -- the factor and its rate of change
local steps = 0
local entry_speed
local lift_saved                   -- {data, bits} while data[0x88] is raised
local pending_drag                 -- {obj, factor} for the end of the step

local function held()
    if force_a then return steps >= force_a and steps <= force_b end
    local pad = ac4.pad(1)
    return pad and pad.connected and (pad.buttons & BUTTONS) == BUTTONS
end

local function restore_lift()
    if lift_saved then
        ac4.write32(lift_saved.d + 0x88, lift_saved.bits)
        lift_saved = nil
    end
end

local function ok_ptr(p) return p >= 0x100000 and p < 0x1FFF000 and p % 4 == 0 end

local function install(address, flight_return)
    local cur
    local before = ac4.hook_before(address, function(ctx)
        cur = nil
        if ac4.reg(ctx, 31) ~= flight_return then return end
        local o, s, d = ac4.arg(ctx, 0), ac4.arg(ctx, 1), ac4.arg(ctx, 2)
        if ok_ptr(o) and ok_ptr(s) and ok_ptr(d) then cur = {o = o, s = s, d = d} end
    end, 5)
    if not before then return false end
    local after = ac4.hook_after(address, function()
        local c = cur
        cur = nil
        if not c then return end
        restore_lift()                       -- in case a step ended unseen
        steps = steps + 1
        local on = held()

        -- critically damped spring towards the target factor
        local target = on and GAIN or 1.0
        local tau = (target > k) and T_IN or T_OUT
        local w = 4.0 / tau                  -- ~98% of the way after tau
        kv = kv + (w * w * (target - k) - 2 * w * kv) * DT
        k = k + kv * DT
        if not on and math.abs(k - 1.0) < 1e-4 and math.abs(kv) < 1e-3 then k, kv = 1.0, 0.0 end
        if k < 1.0 then k = 1.0 end          -- never softer than stock

        local out = ac4.read_float(c.s)
        local aoa = ac4.read_float(c.s + 0x2E8)
        if not (math.abs(out) < 10 and math.abs(aoa) < 10) then return end
        if k ~= 1.0 then
            -- the nose ...
            ac4.write_float(c.s, out * k)
            -- ... and the flight path with it (until the step ends)
            local lift = ac4.read_float(c.d + 0x88)
            if math.abs(lift) < 10 then
                lift_saved = {d = c.d, bits = ac4.read32(c.d + 0x88)}
                ac4.write_float(c.d + 0x88, lift * (1 + (k - 1) * FOLLOW))
            end
        end

        -- the cost: induced drag, growing with the extra G and the AoA squared.
        -- The step rebuilds obj+0x20 from a copy taken before the helpers, so
        -- the velocity is scaled when the step ends (end_step below)
        local vx, vy, vz = ac4.read_float(c.o + 0x20), ac4.read_float(c.o + 0x24),
                           ac4.read_float(c.o + 0x28)
        local speed = math.sqrt(vx * vx + vy * vy + vz * vz)
        if on and not entry_speed then entry_speed = speed end
        if not on and k == 1.0 then entry_speed = nil end
        if BLEED > 0 and entry_speed and speed > 0 and speed < 1000 and k > 1.0 then
            local extra = (k - 1.0) / math.max(GAIN - 1.0, 1e-3)
            local load = math.min(1.5, (aoa / AOA_FULL) ^ 2)
            local f = 1 - BLEED * DT * extra * load
            local floor = entry_speed * FLOOR
            if speed * f < floor then f = math.min(1.0, floor / speed) end
            if f < 1.0 then pending_drag = {o = c.o, f = f} end
        end
        if log_left > 0 then
            log_left = log_left - 1
            ac4.log(string.format("altoG %d k=%.3f out=%.5f->%.5f aoa=%.4f v=%.3f",
                steps, k, out, out * k, aoa, speed))
        end
    end, -30)
    if not after then ac4.unhook(before) return false end
    return true
end

-- as the player's flight step ends: data[0x88] back to stock, and the drag
-- applied to the velocity the step has just rebuilt
local function end_step()
    restore_lift()
    local p = pending_drag
    pending_drag = nil
    if not p then return end
    for off = 0x20, 0x28, 4 do
        local v = ac4.read_float(p.o + off)
        if math.abs(v) < 1000 then ac4.write_float(p.o + off, v * p.f) end
    end
end
local closer = ac4.hook_after(0x00128E80, end_step, -30)

local normal = install(0x001282B8, 0x00129A84)
local alternate = install(0x00128510, 0x001297F8)
ac4.log(string.format("alto G (L1 + R1): giro x%.2f (trayectoria %.0f%%), entrada %.2fs, "
    .. "salida %.2fs, pierde %.0f%%/s, enganches %s/%s/%s", GAIN, FOLLOW * 100, T_IN, T_OUT,
    BLEED * 100, tostring(normal), tostring(alternate), tostring(closer ~= nil)))
