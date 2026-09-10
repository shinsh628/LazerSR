// Port of vendor/leoblack/interlude/layout.js

namespace LazerSR.DanCalculator.Interlude;

public static class Layout
{
    public static int KeysOnLeftHand(int keymode)
    {
        switch (keymode)
        {
            case 3:
            case 4:
                return 2;
            case 5:
            case 6:
                return 3;
            case 7:
            case 8:
                return 4;
            case 9:
            case 10:
                return 5;
            default:
                throw new InvalidOperationException($"Invalid keymode {keymode}");
        }
    }
}
