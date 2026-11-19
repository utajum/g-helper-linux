// NumberPad layout, hit test, gesture and detection tests. Pure data, no sandbox state.

using GHelper.Linux.Input;

namespace GHelper.Linux.Tests;

public static class NumberPadLayoutTests
{
    // Synthetic 130 x 82 mm touchpad at 30 units/mm.
    private const int Res = 30;
    private static readonly TouchpadRange Pad = new(130 * Res, 82 * Res, Res, Res);
    private static readonly TouchpadRange PadNoRes = new(130 * Res, 82 * Res, 0, 0);

    private const string G614Model = "ROG Strix G16 G614JVR_G614JVR";
    private const string G614Touchpad = "ASUE1416:00 04F3:3275 Touchpad";

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- NumberPadLayouts ---");

        Harness.Scenario("Resolve_G614AndG634_UseDriverGH3XLNDZ", _ =>
        {
            foreach (string model in new[]
            {
                G614Model,
                "ROG Strix G16 G614JIR_G614JIR",
                "ROG Strix G614JV_G614JV",
                "ROG Strix G614JZ_G614JZ",
                "ROG Strix G634JZ_G634JZ",
                "ROG Strix G634JY_G634JY",
            })
            {
                var layout = NumberPadLayouts.Resolve(model);
                Harness.AssertEqual("GH3XLNDZ", layout.Name, model);
                Harness.AssertEqual(5, layout.Cols, model + " cols");
                Harness.AssertEqual(4, layout.Rows, model + " rows");
                Harness.AssertEqual(0.75, layout.CornerXMinFrac, model + " x");
                Harness.AssertEqual(0.25, layout.CornerYMaxFrac, model + " y");
                Harness.AssertEqual(496L, layout.HoldMs, model + " hold");
            }
        });

        Harness.Scenario("Resolve_G533_UsesDriverGH2XLNDZ", _ =>
        {
            var layout = NumberPadLayouts.Resolve("ROG Strix G533QS_G533QS");
            Harness.AssertEqual("GH2XLNDZ", layout.Name, "G533 name");
            Harness.AssertEqual(5, layout.Cols, "G533 cols");
            Harness.AssertEqual(4, layout.Rows, "G533 rows");
            Harness.AssertEqual(0.85, layout.CornerXMinFrac, "G533 x zone");
            Harness.AssertEqual(0.20, layout.CornerYMaxFrac, "G533 y zone");
        });

        Harness.Scenario("Resolve_Operator5x4Models_Unchanged", _ =>
        {
            foreach (string model in new[]
            {
                "ASUS Zenbook 14 OLED UX3405MA_UX3405MA",
                "ASUS Zenbook UM3402YA_UM3402YA",
                "ASUS Vivobook B3302ZA_B3302ZA",
            })
            {
                var layout = NumberPadLayouts.Resolve(model);
                Harness.AssertEqual("Operator column", layout.Name, model);
                Harness.AssertEqual(5, layout.Cols, model + " cols");
                Harness.AssertEqual(0.85, layout.CornerXMinFrac, model + " x");
                Harness.AssertEqual(0.20, layout.CornerYMaxFrac, model + " y");
                Harness.AssertEqual(1000L, layout.HoldMs, model + " hold");
            }
        });

        Harness.Scenario("Resolve_Unknown_FallsBackToUniversal", _ =>
        {
            foreach (string touchpad in new[] { "", "SYNA7DB5:00 06CB:CD41 Touchpad", "ASUE1404:00 04F3:3101 Touchpad" })
            {
                var layout = NumberPadLayouts.Resolve("ROG Strix G713PI_G713PI", touchpad);
                Harness.AssertEqual("Universal", layout.Name, "unknown layout " + touchpad);
                Harness.AssertEqual(4, layout.Cols, "unknown cols");
                Harness.AssertEqual(4, layout.Rows, "unknown rows");
                Harness.AssertEqual(0.85, layout.CornerXMinFrac, "unknown x");
                Harness.AssertEqual(0.20, layout.CornerYMaxFrac, "unknown y");
                Harness.AssertEqual(1000L, layout.HoldMs, "unknown hold");
            }
        });

        Harness.Scenario("Resolve_ModelOverrideWinsOverTouchpadId", _ =>
        {
            var layout = NumberPadLayouts.Resolve("ASUS Zenbook 14 OLED UX3405MA_UX3405MA", G614Touchpad);
            Harness.AssertEqual("Operator column", layout.Name, "override");
        });

        Harness.Scenario("Resolve_ChosenLayoutWins", _ =>
        {
            Harness.AssertEqual("Universal", NumberPadLayouts.Resolve(G614Model, G614Touchpad, "Universal").Name, "over model override");
            Harness.AssertEqual("NHMOLFP", NumberPadLayouts.Resolve("ASUS Laptop", G614Touchpad, "NHMOLFP").Name, "over touchpad ID");
            Harness.AssertEqual("GH3XLNDZ", NumberPadLayouts.Resolve(G614Model, G614Touchpad, "NoSuchLayout").Name, "unknown choice is Auto");
        });

        Harness.Scenario("All_ListsEveryLayoutOnce", _ =>
        {
            var names = NumberPadLayouts.All.Select(l => l.Name).ToList();
            Harness.AssertEqual(26, names.Count, "count");
            Harness.AssertEqual(26, names.Distinct().Count(), "unique");
            Harness.Assert(names.All(n => n.Length > 0), "named");
            Harness.AssertEqual("Universal", names[0], "first");
        });

        Harness.Scenario("Resolve_TouchpadId_MatchesDriverTable", _ =>
        {
            var expected = new (string Id, string Layout)[]
            {
                ("ELAN1200", "NH4XLNDZ"), ("ELAN1301", "NH4XLNDZ"), ("ASUP1204", "NH2XLOL"),
                ("ELAN1400", "NHL"), ("ELAN1401", "NHM"), ("ELAN1402", "NHS"),
                ("ELAN1405", "GHM"), ("ELAN1406", "NVM"), ("ELAN1407", "NHXL"),
                ("ASUE1400", "NHL"), ("ASUE1401", "NHM"), ("ASUE1402", "NHS"),
                ("ASUE1403", "GH2XLNDZ"), ("ASUE1405", "GHM"), ("ASUE1406", "NVM"),
                ("ASUE1407", "NHXL"), ("ASUE1408", "GVM_A"), ("ASUE1409", "NHMOL"),
                ("ASUE140A", "NHXLNDZ"), ("ASUE140B", "NHLNDZ"), ("ASUE140C", "NHMNDZ"),
                ("ASUE140D", "NH2XLNDZ"), ("ASUE140E", "NHXLNDZ"), ("ASUE140F", "GVL"),
                ("ASUE1410", "CHXLNDZ"), ("ASUE1411", "NHLOL"), ("ASUE1412", "NH3XLNDZ"),
                ("ASUE1413", "NH4XLNDZ"), ("ASUE1414", "NH2XLOL"), ("ASUE1415", "CH3XLNDZ"),
                ("ASUE1416", "GH3XLNDZ"), ("ASUE1417", "NH2XLNDZ9LED"), ("ASUE1418", "CH3XLNDZ"),
                ("ASUE1800", "NHMOLFP"),
                ("ASUF1407", "NHXL"), ("ASUF1411", "NHLOL"), ("ASUF1415", "NH2XLNDZ9LED"), ("ASUF1416", "GH3XLNDZ"),
                ("ASUP1411", "NHLOL"), ("ASUP1412", "NHMOL"), ("ASUP1413", "NH4XLNDZ"),
                ("ASUP1414", "NH2XLOL"), ("ASUP1415", "NH2XLNDZ9LED"), ("ASUP1416", "NH2XLNDZ"),
                ("ASCF1400", "GH4XLNDZ"), ("ASCP1400", "GH4XLNDZ"),
            };
            foreach (var (id, name) in expected)
            {
                var layout = NumberPadLayouts.Resolve("ASUS Laptop", id + ":00 04F3:3101 Touchpad");
                Harness.AssertEqual(name, layout.Name, id);
                Harness.Assert(layout.Geometry != null, id + " has driver geometry");
                Harness.AssertEqual(layout.Rows * layout.Cols, layout.Cells.Length, id + " cells");
            }
        });

        Harness.Scenario("DriverGrids_MatchWindowsKeyTables", _ =>
        {
            // NH*: % and = in the right column, same as Operator5x4.
            var nh = NumberPadLayouts.Resolve("ASUS Laptop", "ASUE140D:00 04F3:31B9 Touchpad");
            Harness.AssertEqual(NumberPadLayouts.KEY_5, nh.Cells[14]![1], "NH %");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPEQUAL, nh.Cells[19]![0], "NH =");

            // NHL: NumberPad 1.0 uses the Strix grid.
            var nhl = NumberPadLayouts.Resolve("ASUS Laptop", "ASUE1400:00 04F3:3101 Touchpad");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPENTER, nhl.Cells[14]![0], "NHL enter");

            // NVM: 4x5 with = % Backspace Backspace on top.
            var nvm = NumberPadLayouts.Resolve("ASUS Laptop", "ELAN1406:00 04F3:3101 Touchpad");
            Harness.AssertEqual(4, nvm.Cols, "NVM cols");
            Harness.AssertEqual(5, nvm.Rows, "NVM rows");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPEQUAL, nvm.Cells[0]![0], "NVM =");
            Harness.AssertEqual(NumberPadLayouts.KEY_BACKSPACE, nvm.Cells[3]![0], "NVM backspace");
            Harness.AssertEqual(NumberPadLayouts.KEY_KP0, nvm.Cells[16]![0], "NVM 0");

            // GV*: classic 4x5, NumLock zone top-left over the empty cell 0.
            var gvl = NumberPadLayouts.Resolve("ASUS Laptop", "ASUE140F:00 04F3:3101 Touchpad");
            Harness.AssertEqual(4, gvl.Cols, "GVL cols");
            Harness.Assert(gvl.Cells[0] == null, "GVL cell 0 empty");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPPLUS, gvl.Cells[7]![0], "GVL +");
            Harness.Assert(gvl.Geometry!.LockOnLeft, "GVL lock on left");
        });

        Harness.Scenario("G614_CellsMatchPrintedLayout", _ =>
        {
            var cells = NumberPadLayouts.Resolve(G614Model).Cells;
            Harness.AssertEqual(20, cells.Length, "cell count");

            Harness.AssertEqual(NumberPadLayouts.KEY_KP7, cells[0]![0], "7");
            Harness.AssertEqual(NumberPadLayouts.KEY_KP8, cells[1]![0], "8");
            Harness.AssertEqual(NumberPadLayouts.KEY_KP9, cells[2]![0], "9");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPSLASH, cells[3]![0], "/");
            Harness.AssertEqual(NumberPadLayouts.KEY_BACKSPACE, cells[4]![0], "numlk/backspace");

            Harness.AssertEqual(NumberPadLayouts.KEY_KPASTERISK, cells[8]![0], "*");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPMINUS, cells[13]![0], "-");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPPLUS, cells[18]![0], "+");

            // Wide zero spans the first two cells of the bottom row.
            Harness.AssertEqual(NumberPadLayouts.KEY_KP0, cells[15]![0], "0 left");
            Harness.AssertEqual(NumberPadLayouts.KEY_KP0, cells[16]![0], "0 right");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPDOT, cells[17]![0], ".");

            // Tall Enter occupies the bottom two rows of the right column.
            Harness.AssertEqual(NumberPadLayouts.KEY_KPENTER, cells[14]![0], "enter top");
            Harness.AssertEqual(NumberPadLayouts.KEY_KPENTER, cells[19]![0], "enter bottom");
        });

        Harness.Scenario("HitTest_G614_UsesDriverMillimetres", _ =>
        {
            var g614 = NumberPadLayouts.Resolve(G614Model);
            // Columns: border 13, 17, 23, 23, 23, 20, border 11. Rows: border 9, 15, 19, 19, 20.
            AssertHit(g614, 109, 15, true, -1, "NUMLK label never types");
            AssertHit(g614, 109, 35, false, 9, "backspace under the label");
            AssertHit(g614, 88, 15, false, 3, "slash stays clear of the label");
            AssertHit(g614, 20, 15, false, 0, "7");
            AssertHit(g614, 20, 70, false, 15, "0");
            AssertHit(g614, 5, 15, false, -1, "left border");
            AssertHit(g614, 125, 35, false, -1, "right border");
            AssertHit(g614, 20, 5, false, -1, "top border");
        });

        Harness.Scenario("HitTest_G614JVR_ReportedHoldPointToggles", _ =>
        {
            // #255 hardware report: hold at 3189,360 on a 3987x2579 pad; the resolution is not in the report.
            var g614 = NumberPadLayouts.Resolve(G614Model, G614Touchpad);
            foreach (int res in new[] { 0, 30, 31, 32, 33 })
            {
                var hit = new NumberPadSurface(g614, new TouchpadRange(3987, 2579, res, res)).Hit(3189, 360);
                Harness.AssertEqual(new NumberPadHit(true, -1), hit, "hold point at " + res + " units/mm");
            }
        });

        Harness.Scenario("HitTest_NoResolution_UsesFractions", _ =>
        {
            var g614 = NumberPadLayouts.Resolve(G614Model);
            var hit = new NumberPadSurface(g614, PadNoRes).Hit(PadNoRes.XMax * 9 / 10, PadNoRes.YMax / 10);
            Harness.AssertEqual(new NumberPadHit(true, -1), hit, "driver corner never types");

            hit = new NumberPadSurface(NumberPadLayouts.Universal4x4, PadNoRes).Hit(PadNoRes.XMax * 9 / 10, PadNoRes.YMax / 10);
            Harness.AssertEqual(new NumberPadHit(true, 3), hit, "universal corner still types");
        });

        Harness.Scenario("HitTest_LeftLockZone", _ =>
        {
            var gvl = NumberPadLayouts.Resolve("ASUS Laptop", "ASUE140F:00 04F3:3101 Touchpad");
            AssertHit(gvl, 8, 15, true, -1, "GVL lock top-left");
            AssertHit(gvl, 125, 15, false, 3, "GVL minus top-right");
        });

        Harness.Scenario("HitTest_CalculatorZoneNeverTypes", _ =>
        {
            // NHXL: calculator zone 10 x 12 mm, key 7 starts 8 mm left and 4 mm down.
            var nhxl = NumberPadLayouts.Resolve("ASUS Laptop", Nhxl);
            AssertHit(nhxl, 9, 8, false, -1, "calculator icon", calc: true);
            AssertHit(nhxl, 12, 8, false, 0, "7 outside the icon");
            var g614 = NumberPadLayouts.Resolve(G614Model);
            AssertHit(g614, 14, 10, false, 0, "GH3XLNDZ has no calculator zone");
        });

        Harness.Scenario("Gesture_HoldAsksForToggleAndConsumesTouch", _ =>
        {
            var g = Idle();
            Down(g, 0, 109, 15, 0);
            Harness.AssertEqual(496L, g.MsUntilDue(0), "hold pending");
            Harness.AssertEqual(0, g.Tick(495).Count, "not yet");
            Harness.Assert(Kinds(g.Tick(496)) == "Toggle", "hold fires");
            Harness.Assert(!g.Active, "owner decides the state");
            Harness.AssertEqual("", Kinds(Up(g, 0, 600)), "release after hold types nothing");
            Harness.AssertEqual(-1L, g.MsUntilDue(600), "no timer pending");
        });

        Harness.Scenario("Gesture_QuickTapTypesKey", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            var o = Up(g, 0, 1050);
            Harness.AssertEqual("KeyDown KeyUp", Kinds(o), "tap");
            Harness.AssertEqual(NumberPadLayouts.KEY_KP8, o[0].Keys![0], "8 typed");
        });

        Harness.Scenario("Gesture_RestPressesKeyUntilLift", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            Harness.AssertEqual(80L, g.MsUntilDue(1000), "Gesture_TouchSendKey_Trig_FC");
            Harness.AssertEqual("KeyDown", Kinds(g.Tick(1080)), "pressed after rest");
            Harness.AssertEqual("KeyUp", Kinds(Up(g, 0, 1500)), "released on lift");
        });

        Harness.Scenario("Gesture_SecondFingerDelaysKey", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            Down(g, 1, 64, 35, 1000);
            Harness.AssertEqual(104L, g.MsUntilDue(1000), "3 frames more for two fingers");
        });

        Harness.Scenario("Gesture_ShortTapOnNumLockTypesNothing", _ =>
        {
            var g = Activated();
            Down(g, 0, 109, 15, 1000);
            Harness.AssertEqual("", Kinds(Up(g, 0, 1100)), "no backspace");
            Harness.AssertEqual(0, g.Tick(2000).Count, "no toggle");
        });

        Harness.Scenario("Gesture_UniversalCornerTapStillTypes", _ =>
        {
            var g = new NumberPadGesture(new NumberPadSurface(NumberPadLayouts.Universal4x4, Pad));
            g.SetActive(true);
            Down(g, 0, 120, 5, 1000);
            var o = Up(g, 0, 1100);
            Harness.AssertEqual("KeyDown KeyUp", Kinds(o), "quick tap types");
            Harness.AssertEqual(NumberPadLayouts.KEY_BACKSPACE, o[0].Keys![0], "backspace");
            Down(g, 0, 120, 5, 2000);
            Harness.AssertEqual("Toggle", Kinds(g.Tick(3000)), "hold toggles");
            Harness.AssertEqual("", Kinds(Up(g, 0, 3100)), "no key after hold");
        });

        Harness.Scenario("Gesture_IdleTouchIsLeftAlone", _ =>
        {
            var g = Idle();
            Harness.AssertEqual("", Kinds(Down(g, 0, 41, 15, 0)), "no pointer while idle");
            Harness.AssertEqual("", Kinds(Up(g, 0, 50)), "no key while idle");
        });

        Harness.Scenario("Gesture_LeavingZoneCancelsHold", _ =>
        {
            var g = Idle();
            Down(g, 0, 109, 15, 0);
            Move(g, 0, 50, 15, 100);
            Harness.AssertEqual(-1L, g.MsUntilDue(100), "hold cancelled");
            Harness.AssertEqual(0, g.Tick(1000).Count, "no toggle");
        });

        Harness.Scenario("Gesture_SmallSlideTypesKeyUnderLift", _ =>
        {
            var g = Activated();
            Down(g, 0, 50, 15, 1000);
            Move(g, 0, 53, 15, 1010);
            var o = Up(g, 0, 1050);
            Harness.AssertEqual(NumberPadLayouts.KEY_KP9, o[0].Keys![0], "lift on 9 types 9, as on Windows");
        });

        Harness.Scenario("Gesture_MoveBecomesPointerWithHistory", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            Move(g, 0, 43, 15, 1008);
            var o = Move(g, 0, 50, 20, 1016);
            Harness.AssertEqual("Pointer Pointer Pointer", Kinds(o), "replay two frames, then the current one");
            Harness.AssertEqual(41 * Res, o[0].Frame!.Contacts[0].X, "replay starts at touch-down");
            Harness.AssertEqual(50 * Res, o[2].Frame!.Contacts[0].X, "current position");
            Harness.AssertEqual(-1L, g.MsUntilDue(1016), "no key timer");
            Harness.AssertEqual("Pointer", Kinds(Up(g, 0, 1100)), "lift goes to the pointer");
        });

        Harness.Scenario("Gesture_TwoFingerScrollOnKeysIsPointer", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 30, 1000);
            Down(g, 1, 64, 30, 1000);
            var o = new List<NumberPadOutput>();
            for (int i = 1; i <= 8; i++)
            {
                o.AddRange(Move(g, 0, 41, 30 + i, 1000 + i * 8));
                o.AddRange(Move(g, 1, 64, 30 + i, 1000 + i * 8));
                o.AddRange(g.Tick(1000 + i * 8));
            }
            o.AddRange(Up(g, 1, 1100));
            o.AddRange(Up(g, 0, 1100));
            Harness.Assert(o.Count > 0 && o.All(x => x.Kind == NumberPadEvent.Pointer), "no key typed");
            Harness.AssertEqual(2, o.Max(x => x.Frame!.Contacts.Count), "both fingers go to the pointer");
        });

        Harness.Scenario("Gesture_TwoFingerScrollAfterFingerOffKeysIsPointer", _ =>
        {
            // Slot 0 holds a finger off the keys: a pointer on the border, or the NumLock hold.
            foreach (var (x, y) in new[] { (5, 40), (109, 15) })
            {
                var g = Activated();
                Down(g, 0, x, y, 1000);
                Down(g, 1, 41, 30, 1000);
                Down(g, 2, 64, 30, 1000);
                var o = new List<NumberPadOutput>();
                for (int i = 1; i <= 8; i++)
                {
                    o.AddRange(Move(g, 1, 41, 30 + i, 1000 + i * 8));
                    o.AddRange(Move(g, 2, 64, 30 + i, 1000 + i * 8));
                    o.AddRange(g.Tick(1000 + i * 8));
                }
                o.AddRange(g.Tick(1200));
                o.AddRange(Up(g, 2, 1300));
                o.AddRange(Up(g, 1, 1300));
                Harness.Assert(o.All(e => e.Kind == NumberPadEvent.Pointer), $"no key typed, slot 0 at {x},{y} mm");
            }
        });

        Harness.Scenario("Gesture_TouchOutsideKeysIsPointer", _ =>
        {
            var g = Activated();
            Harness.AssertEqual("Pointer", Kinds(Down(g, 0, 5, 40, 1000)), "left border");
        });

        Harness.Scenario("Gesture_TapRightAfterPointerStaysPointer", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            Move(g, 0, 50, 15, 1010);
            Up(g, 0, 1100);
            Harness.AssertEqual("Pointer", Kinds(Down(g, 0, 52, 16, 1300)), "within 350 ms and 10 mm");
            Up(g, 0, 1320);
            Harness.AssertEqual("", Kinds(Down(g, 0, 52, 16, 1700)), "late touch is a key again");
        });

        Harness.Scenario("Gesture_ClickTurnsKeyTouchIntoPointer", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            g.Feed(EvdevInterop.EV_KEY, EvdevInterop.BTN_LEFT, 1, 1010);
            var o = g.Feed(EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0, 1010).ToList();
            Harness.AssertEqual("Pointer", Kinds(o), "clicked");
            Harness.Assert(o[0].Frame!.Button, "button forwarded");
        });

        Harness.Scenario("Gesture_SetActiveOffReleasesKeysAndPointer", _ =>
        {
            var g = Activated();
            Down(g, 0, 41, 15, 1000);
            g.Tick(1080);
            Down(g, 1, 5, 40, 1090);
            var o = g.SetActive(false);
            Harness.AssertEqual("KeyUp Pointer", Kinds(o), "release and lift");
            Harness.AssertEqual(0, o[1].Frame!.Contacts.Count, "pointer lifted");
            Harness.AssertEqual("", Kinds(Up(g, 0, 1200)), "no second release");
        });

        Harness.Scenario("Gesture_CalculatorHoldAsksForBrightnessWhenActive", _ =>
        {
            var g = new NumberPadGesture(new NumberPadSurface(NumberPadLayouts.Resolve("ASUS Laptop", Nhxl), Pad));
            Down(g, 0, 5, 5, 0);
            Harness.AssertEqual(0, g.Tick(1000).Count, "idle: no brightness");
            Up(g, 0, 1100);
            g.SetActive(true);
            Down(g, 0, 5, 5, 2000);
            Harness.AssertEqual(320L, g.MsUntilDue(2000), "NumAdv_Trigger_FC");
            Harness.AssertEqual("Brightness", Kinds(g.Tick(2320)), "brightness");
            Harness.AssertEqual("", Kinds(Up(g, 0, 2400)), "icon never types");
        });

        Harness.Scenario("Gesture_SlideFromCalculatorOpensCalculator", _ =>
        {
            var g = new NumberPadGesture(new NumberPadSurface(NumberPadLayouts.Resolve("ASUS Laptop", Nhxl), Pad));
            Down(g, 0, 5, 5, 0);
            Move(g, 0, 15, 5, 10);
            Harness.AssertEqual("", Kinds(Move(g, 0, 21, 5, 20)), "6 mm past the icon edge");
            Harness.AssertEqual("Calculator", Kinds(Move(g, 0, 23, 5, 30)), "more than 7 mm");
            Harness.AssertEqual("", Kinds(Move(g, 0, 40, 5, 40)), "only once");
        });

        Harness.Scenario("Gesture_SlideFromNumLockOnlyOnOlLayouts", _ =>
        {
            var ol = new NumberPadGesture(new NumberPadSurface(NumberPadLayouts.Resolve("ASUS Laptop", "ASUE1411:00 04F3:3101 Touchpad"), Pad));
            Down(ol, 0, 125, 5, 0);
            Move(ol, 0, 115, 5, 10);
            Harness.AssertEqual("Calculator", Kinds(Move(ol, 0, 100, 5, 20)), "NHLOL slide");

            var gh = Idle();
            Down(gh, 0, 109, 15, 0);
            Move(gh, 0, 95, 15, 10);
            Harness.AssertEqual("", Kinds(Move(gh, 0, 70, 15, 20)), "GH3XLNDZ has no slide");
        });

        Harness.Scenario("PointerWriter_EncodesProtocolB", _ =>
        {
            var w = new PointerWriter();
            var e = w.Encode(new PointerFrame(new[] { new PointerContact(2, 100, 200) }, false));
            Harness.AssertEqual("3:47=2 3:57=0 3:53=100 3:54=200 1:330=1 1:325=1 3:0=100 3:1=200 0:0=0", Events(e), "touch down");
            e = w.Encode(new PointerFrame(new[] { new PointerContact(2, 110, 200), new PointerContact(4, 5, 6) }, true));
            Harness.AssertEqual("3:47=2 3:53=110 3:47=4 3:57=1 3:53=5 3:54=6 1:325=0 1:333=1 3:0=110 3:1=200 1:272=1 0:0=0", Events(e), "second finger and click");
            e = w.Encode(new PointerFrame(Array.Empty<PointerContact>(), false));
            Harness.AssertEqual("3:47=2 3:57=-1 3:47=4 3:57=-1 1:333=0 1:330=0 1:272=0 0:0=0", Events(e), "all lifted");
        });

        Harness.Scenario("FindTarget_PicksTouchpadBlock", _ =>
        {
            string text = Block("ASUE1416:00 04F3:3275 Mouse", "i2c-ASUE1416:00", 0, "mouse0 event6")
                + Block("ASUE1416:00 04F3:3275 Touchpad", "i2c-ASUE1416:00", 0, "mouse1 event7");
            var t = NumberPad.FindTarget(text, isAsus: true);
            Harness.Assert(t != null, "found");
            Harness.AssertEqual("ASUE1416:00 04F3:3275 Touchpad", t!.Name, "name");
            Harness.AssertEqual("/dev/i2c-0", t.I2cPath, "bus");
            Harness.AssertEqual(0x15, t.I2cAddr, "addr");
            Harness.AssertEqual("/dev/input/event7", t.EventPath, "event");
        });

        Harness.Scenario("NumLockDevices_ListsNumLockLeds", _ =>
        {
            string text = "N: Name=\"AT Translated Set 2 keyboard\"\nS: Sysfs=/devices/platform/i8042/serio0/input/input3\nH: Handlers=sysrq kbd event3 leds \nB: LED=7\n\n"
                + "N: Name=\"Asus WMI hotkeys\"\nS: Sysfs=/devices/platform/asus-nb-wmi/input/input7\nH: Handlers=rfkill kbd event7 \n\n"
                + "N: Name=\"caps only\"\nS: Sysfs=/devices/virtual/input/input40\nH: Handlers=kbd event8 \nB: LED=2\n\n"
                + "N: Name=\"g-helper numberpad\"\nS: Sysfs=/devices/virtual/input/input41\nH: Handlers=event9 kbd leds \nB: LED=1\n\n";
            var d = NumberPad.NumLockDevices(text);
            Harness.AssertEqual(2, d.Count, "only devices with a NumLock LED");
            Harness.AssertEqual("/dev/input/event3", d["/devices/platform/i8042/serio0/input/input3"], "keyboard node");
            Harness.AssertEqual("/dev/input/event9", d["/devices/virtual/input/input41"], "event as first handler");
        });

        Harness.Scenario("FindTarget_AddressAndFamilies", _ =>
        {
            var asuf = NumberPad.FindTarget(Block("ASUF1416:00 2808:0108 Touchpad", "i2c-ASUF1416:00", 1, "event9"), true);
            Harness.AssertEqual(0x38, asuf!.I2cAddr, "ASUF1416 addr");

            var ascf = NumberPad.FindTarget(Block("ASCF1400:00 2808:0242 Touchpad", "i2c-ASCF1400:00", 2, "event5"), true);
            Harness.AssertEqual("/dev/i2c-2", ascf!.I2cPath, "ASCF detected");

            string elan = Block("ELAN9009:00 04F3:4067 Touchpad", "i2c-ELAN9009:00", 3, "event3")
                + Block("ELAN1406:00 04F3:3101 Touchpad", "i2c-ELAN1406:00", 4, "event4");
            Harness.Assert(NumberPad.FindTarget(elan, isAsus: false) == null, "ELAN needs ASUS");
            Harness.AssertEqual("/dev/i2c-4", NumberPad.FindTarget(elan, isAsus: true)!.I2cPath, "ELAN9009 skipped");
        });
    }

    private const string Nhxl = "ASUE1407:00 04F3:3101 Touchpad";
    private static int _trackingId;

    private static void AssertHit(NumberPadLayout layout, int xMm, int yMm, bool toggle, int cell, string what, bool calc = false)
        => Harness.AssertEqual(new NumberPadHit(toggle, cell, calc), new NumberPadSurface(layout, Pad).Hit(xMm * Res, yMm * Res), what);

    private static NumberPadGesture Idle() => new(new NumberPadSurface(NumberPadLayouts.Resolve(G614Model), Pad));

    private static NumberPadGesture Activated()
    {
        var g = Idle();
        g.SetActive(true);
        return g;
    }

    private static List<NumberPadOutput> Down(NumberPadGesture g, int slot, int xMm, int yMm, long now)
    {
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, slot, now);
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_TRACKING_ID, ++_trackingId, now);
        return Move(g, slot, xMm, yMm, now);
    }

    private static List<NumberPadOutput> Move(NumberPadGesture g, int slot, int xMm, int yMm, long now)
    {
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, slot, now);
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_POSITION_X, xMm * Res, now);
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_POSITION_Y, yMm * Res, now);
        return g.Feed(EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0, now).ToList();
    }

    private static List<NumberPadOutput> Up(NumberPadGesture g, int slot, long now)
    {
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, slot, now);
        g.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_TRACKING_ID, -1, now);
        return g.Feed(EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0, now).ToList();
    }

    private static string Kinds(IReadOnlyList<NumberPadOutput> o) => string.Join(" ", o.Select(x => x.Kind));

    private static string Events(List<(ushort Type, ushort Code, int Value)> e) => string.Join(" ", e.Select(x => $"{x.Type}:{x.Code}={x.Value}"));

    private static string Block(string name, string acpi, int bus, string handlers) =>
        $"I: Bus=0018 Vendor=04f3 Product=3275 Version=0100\n"
        + $"N: Name=\"{name}\"\n"
        + $"P: Phys={acpi}\n"
        + $"S: Sysfs=/devices/pci0000:00/0000:00:15.{bus}/i2c_designware.{bus}/i2c-{bus}/{acpi}/0018:04F3:3275.0001/input/input1{bus}\n"
        + "U: Uniq=\n"
        + $"H: Handlers={handlers} \n"
        + "B: PROP=5\n\n";
}
