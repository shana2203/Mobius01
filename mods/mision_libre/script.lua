-- Free Mission on the title menu before finishing the campaign (SLUS-20152).
--
-- 0022D3C0 builds the title menus: it works out a mask of hidden items per
-- menu (bit = hidden) from the campaign flags at 002A0318 and passes the masks
-- to 0022ABC0 (a2 = the title menu's descriptor 0039D5B0, a4 = the masks).
-- Bit 4 of the first mask is FREE MISSION, hidden until bit 0 of 002A0318
-- (campaign finished) is set.  Clearing that one bit shows the item; nothing
-- in the save changes.  Once inside, the mission list already lets every
-- mission be chosen.
local TITLE_MENU = 0x0039D5B0
local FREE_MISSION = 0x10

assert(ac4.hook_before(0x0022ABC0, function(ctx)
    if ac4.arg(ctx, 2) ~= TITLE_MENU then return end
    local masks = ac4.arg(ctx, 4)
    if masks < 0x00100000 or masks > 0x01FFFFFC then return end
    local v = ac4.read32(masks)
    if v & FREE_MISSION ~= 0 then ac4.write32(masks, v & ~FREE_MISSION) end
end), "No se pudo activar Mision libre")
