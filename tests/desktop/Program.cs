var cases = new (string name, long style, long extended, string process, bool expected)[] {
    ("Chrome", 0x00CF0000, 0, "chrome", true),
    ("Explorer", 0x00CF0000, 0x100, "explorer", true),
    ("NVIDIA ordinary styles", 0x00CF0000, 0, "NVIDIA Overlay", false),
    ("Legacy NVIDIA", 0x00CF0000, 0, "nvsphelper64", false),
    ("Tool HUD", 0x00CF0000, 0x80, "hud", false),
    ("No activate HUD", 0x00CF0000, 0x08000000, "hud", false),
    ("Layered HUD", 0x00CF0000, 0x80000, "hud", false),
    ("Borderless HUD", 0x80000000, 0, "hud", false),
    ("Child window", 0x40020000, 0, "chrome", false),
    ("Steam", 0x00CF0000, 0, "steam", false),
    ("Steam renderer", 0x00CF0000, 0, "steamwebhelper", false),
};
foreach (var c in cases) {
    if (DesktopWindowPolicy.ShouldMinimize(c.style, c.extended, c.process) != c.expected)
        throw new Exception("Failed: " + c.name);
    Console.WriteLine("PASS: " + c.name);
}
