// NumberPad layout selection tests. Pure static data, no sandbox state.

using GHelper.Linux.Input;

namespace GHelper.Linux.Tests;

public static class NumberPadLayoutTests
{
    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- NumberPadLayouts ---");

        Harness.Scenario("ForProduct_G614JVR_UsesStrix5x4", _ =>
        {
            var layout = NumberPadLayouts.ForProduct("ROG Strix G16 G614JVR_G614JVR");
            Harness.AssertEqual("ROG G614/G634", layout.Name, "G614 layout");
            Harness.AssertEqual(5, layout.Cols, "G614 cols");
            Harness.AssertEqual(4, layout.Rows, "G614 rows");
        });

        Harness.Scenario("ForProduct_G614AndG634_UseWideCorner5x4", _ =>
        {
            foreach (string model in new[]
            {
                "ROG Strix G16 G614JIR_G614JIR",
                "ROG Strix G614JV_G614JV",
                "ROG Strix G614JZ_G614JZ",
                "ROG Strix G634JZ_G634JZ",
                "ROG Strix G634JY_G634JY",
            })
            {
                var layout = NumberPadLayouts.ForProduct(model);
                Harness.AssertEqual("ROG G614/G634", layout.Name, model);
                Harness.AssertEqual(0.75, layout.CornerXMinFrac, model + " x");
                Harness.AssertEqual(0.25, layout.CornerYMaxFrac, model + " y");
            }
        });

        Harness.Scenario("ForProduct_G533_Unchanged", _ =>
        {
            var layout = NumberPadLayouts.ForProduct("ROG Strix G533QS_G533QS");
            Harness.AssertEqual("ROG G533", layout.Name, "G533 name");
            Harness.AssertEqual(5, layout.Cols, "G533 cols");
            Harness.AssertEqual(4, layout.Rows, "G533 rows");
            Harness.AssertEqual(0.85, layout.CornerXMinFrac, "G533 x zone");
            Harness.AssertEqual(0.20, layout.CornerYMaxFrac, "G533 y zone");
        });

        Harness.Scenario("ForProduct_Operator5x4Models_Unchanged", _ =>
        {
            foreach (string model in new[]
            {
                "ASUS Zenbook 14 OLED UX3405MA_UX3405MA",
                "ASUS Zenbook UM3402YA_UM3402YA",
                "ASUS Vivobook B3302ZA_B3302ZA",
            })
            {
                var layout = NumberPadLayouts.ForProduct(model);
                Harness.AssertEqual("Operator column", layout.Name, model);
                Harness.AssertEqual(5, layout.Cols, model + " cols");
                Harness.AssertEqual(0.85, layout.CornerXMinFrac, model + " x");
                Harness.AssertEqual(0.20, layout.CornerYMaxFrac, model + " y");
            }
        });

        Harness.Scenario("ForProduct_Unknown_FallsBackToUniversal", _ =>
        {
            var layout = NumberPadLayouts.ForProduct("ROG Strix G713PI_G713PI");
            Harness.AssertEqual("Universal", layout.Name, "unknown layout");
            Harness.AssertEqual(4, layout.Cols, "unknown cols");
            Harness.AssertEqual(4, layout.Rows, "unknown rows");
            Harness.AssertEqual(0.85, layout.CornerXMinFrac, "unknown x");
            Harness.AssertEqual(0.20, layout.CornerYMaxFrac, "unknown y");
        });

        Harness.Scenario("G614_CellsMatchPrintedLayout", _ =>
        {
            var cells = NumberPadLayouts.ForProduct("ROG Strix G16 G614JVR_G614JVR").Cells;
            Harness.AssertEqual(20, cells.Length, "cell count");

            // Physical G614 printed rows, per the asus-numberpad-driver g533 map.
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
    }
}
