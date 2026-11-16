-- SLUS-20152: six 64-bit tables, 21 two-bit aircraft entries each.
-- Tables 0..2 are aircraft liveries, 3..5 their special weapon slots.
-- 00226580 advances availability to 2; ownership uses state 3.
local function unlock_shop()
    for category = 0, 5 do
        local base = 0x002A0320 + category * 8
        for aircraft = 0, 20 do
            local address = base + (aircraft // 16) * 4
            local shift = (aircraft % 16) * 2
            local word = ac4.read32(address)
            local state = (word >> shift) & 3
            if state < 2 then
                word = (word & ~(3 << shift)) | (2 << shift)
                ac4.write32(address, word & 0xFFFFFFFF)
            end
        end
    end
end

assert(ac4.every(1, unlock_shop), "No se pudo activar la tienda completa")
