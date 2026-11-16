-- Campaign credit balance for AC04 NTSC-U (SLUS-20152).
local minimum = math.floor(math.max(0, math.min(99999999, tonumber(ac4.config("credits")) or 0)))
if minimum > 0 then
    local credits_address = 0x002A0D30
    assert(ac4.every(1, function()
        local balance = ac4.read32(credits_address)
        if balance < minimum then ac4.write32(credits_address, minimum) end
    end), "No se pudo activar el ajuste de creditos")
end

-- SLUS-20152: the loadout of each aircraft is a 16-byte record in a table of 23
-- at 002A1CD8 (cannon u16, missiles u8, then three special-weapon slots of
-- 4 bytes: type, ?, count, 0; type 0x0F = empty).  0011EC48 copies the record
-- into the live counters when a mission starts and the hangar shows the same
-- record, so scaling the table once makes both show the bonus.
-- cannon up to +500%, missiles up to +300%, the special weapon up to +800%
-- (the counters still stop at 9999 / 255 / 99, the room the game has)
local function bonus(key, limit)
    return math.max(0, math.min(limit, tonumber(ac4.config(key)) or 0))
end

local gun, missiles, special = bonus("gun", 500), bonus("missiles", 300), bonus("special", 800)
if gun == 0 and missiles == 0 and special == 0 then return end

local TABLE, COUNT, STRIDE = 0x002A1CD8, 23, 0x10

local function scale(value, percent, limit)
    return math.min(limit, math.floor(value * (100 + percent) / 100 + 0.5))
end

-- the stock table, checked before touching it: every record has a plausible
-- cannon count and slot types the game knows
local function stock_table()
    for i = 0, COUNT - 1 do
        local r = TABLE + i * STRIDE
        local g = ac4.read16(r)
        if g < 100 or g > 2000 then return false end
        for s = 0, 2 do
            local t = ac4.read8(r + 4 + s * 4)
            if t > 0x0F then return false end
        end
    end
    return true
end

local done = false
local function apply()
    if done then return true end
    if not stock_table() then return false end
    for i = 0, COUNT - 1 do
        local r = TABLE + i * STRIDE
        ac4.write16(r, scale(ac4.read16(r), gun, 9999))
        ac4.write8(r + 2, scale(ac4.read8(r + 2), missiles, 255))
        for s = 0, 2 do
            local slot = r + 4 + s * 4
            if ac4.read8(slot) ~= 0x0F then
                ac4.write8(slot + 2, scale(ac4.read8(slot + 2), special, 99))
            end
        end
    end
    done = true
    ac4.log(string.format("municion: tabla de %d aviones aumentada (canon +%d%%, misiles +%d%%, especial +%d%%)",
                          COUNT, gun, missiles, special))
    return true
end

-- the executable's data is in place from the start; retry a few fields in case
local tries = 0
assert(ac4.every(1, function()
    if done or tries > 600 then return end
    tries = tries + 1
    apply()
end), "No se pudo activar la municion extra")
