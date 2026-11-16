-- SLUS-20152: 0014CC70 computes the steering correction for guided projectiles.
-- At its call to 00102EF0, a2 points to the correction at projectile + 0x40.
-- The source aircraft slot is the byte at projectile + 0x58.
local multiplier = tonumber(ac4.config("turn_multiplier")) or 1.75
multiplier = math.max(0, math.min(2.0, multiplier))
if multiplier == 1 then return end

local saved_address, saved_bits
local after = ac4.hook_after(0x00102EF0, function(ctx)
    if not saved_address or ac4.reg(ctx, 31) ~= 0x0014D018 then return end
    ac4.write32(saved_address, saved_bits)
    saved_address, saved_bits = nil, nil
end)
if not after then error("No se pudo registrar la restauracion del guiado") end

local before = ac4.hook_before(0x00102EF0, function(ctx)
    if saved_address or ac4.reg(ctx, 31) ~= 0x0014D018 then return end
    local correction = ac4.arg(ctx, 2)
    if correction < 0x100040 or correction > 0x01FFFFE0 or correction % 16 ~= 0 then return end
    if ac4.read8(correction + 0x18) ~= 0 then return end
    local angle = ac4.read_float(correction)
    if not (math.abs(angle) > 0 and math.abs(angle) < math.pi) then return end
    saved_address, saved_bits = correction, ac4.read32(correction)
    ac4.write_float(correction, math.max(-math.pi, math.min(math.pi, angle * multiplier)))
end)
if not before then
    ac4.unhook(after)
    error("No se pudo registrar el ajuste de guiado")
end
