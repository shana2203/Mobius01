-- AC04 NTSC-U. These two helpers compute pitch during the local flight step.
local boost = tonumber(ac4.config("pitch_boost")) or 1.25
boost = math.max(1, math.min(1.6, boost))
if boost == 1 then return end

-- Both helpers read these coefficients from the selected aircraft's flight data.
-- Restore their exact bits on return so other callers retain the stock data.
local offsets = {0x78, 0x7C, 0x8C}
local function install(address, flight_return)
    local saved
    local after = ac4.hook_after(address, function()
        if not saved then return end
        for i, offset in ipairs(offsets) do
            ac4.write32(saved.base + offset, saved[i])
        end
        saved = nil
    end)
    if not after then return false end

    local before = ac4.hook_before(address, function(ctx)
        if saved or ac4.reg(ctx, 31) ~= flight_return then return end
        local base = ac4.arg(ctx, 2)
        if base < 0x100000 or base > 0x1FFFF50 or base % 4 ~= 0 then return end

        local values = {}
        for i, offset in ipairs(offsets) do
            local value = ac4.read_float(base + offset)
            if not (math.abs(value) < 100) then return end
            values[i] = value
        end

        saved = {base = base}
        for i, offset in ipairs(offsets) do
            saved[i] = ac4.read32(base + offset)
            ac4.write_float(base + offset, values[i] * boost)
        end
    end)
    if not before then
        ac4.unhook(after)
        return false
    end
    return true
end

local normal = install(0x001282B8, 0x00129A84)
local alternate = install(0x00128510, 0x001297F8)
ac4.log(string.format("maniobrabilidad: cabeceo x%.2f, enganches %s/%s",
    boost, tostring(normal), tostring(alternate)))

