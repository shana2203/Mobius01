-- The targeting pass reads the aircraft's range limit at +0x1B4.
local range_multiplier = tonumber(ac4.config("standard_lock_range_multiplier")) or 1.30
local range_address, original_range
local restore = ac4.hook_after(0x001232D8, function()
    if not range_address then return end
    ac4.write32(range_address, original_range)
    range_address, original_range = nil, nil
end)
if restore then
    local apply = ac4.hook_before(0x001232D8, function(ctx)
        if range_address or ac4.arg(ctx, 0) ~= 0 then return end
        local slot = ac4.read32(0x002A1F70)
        if slot < 0x100000 or slot > 0x01FFFFB8 or slot % 4 ~= 0 then return end
        local aircraft = ac4.read32(slot + 0x44)
        if aircraft < 0x100000 or aircraft > 0x01FFFE48 or aircraft % 4 ~= 0 then return end
        if (ac4.read32(aircraft + 0x14C) & 0x8000) ~= 0 then return end

        local address = aircraft + 0x1B4
        local range = ac4.read_float(address)
        if not (range > 0 and range <= 1000000) then return end
        range_address, original_range = address, ac4.read32(address)
        ac4.write_float(address, range * math.max(1, math.min(2, range_multiplier)))
    end)
    if not apply then ac4.unhook(restore) end
end

