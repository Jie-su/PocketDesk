using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestUuControllerParsing()
    {
        var observed = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
        UuControllerNode Node(string id, string name = "", params UuControllerNode[] children) => new("", id, name, children);
        UuControllerNode Item(string name) => Node("ControllerInfoView.ControllerItemWidget", "",
            Node("ControllerInfoView.ControllerItemWidget.controllerNameLabel", name));
        UuControllerNode Scope(string title, int? count, params UuControllerNode[] items)
        {
            var fields = new List<UuControllerNode> { Node("ControllerInfoView.titleLabel", title) };
            if (count is not null) fields.Add(Node("ControllerInfoView.textLabel", $"当前有{count}台设备正在控制此电脑"));
            fields.AddRange(items);
            return Node("ControllerInfoView", "", fields.ToArray());
        }
        UuControllerSnapshot Parse(params UuControllerNode[] children) =>
            UuControllerService.ParseTree(Node("HomePageMainWindow", "", children), observed);

        Test("UU parsing reads the single name only from the local controlled-computer scope", () =>
        {
            var result = Parse(Scope("本机被控中", 1, Item("iPad Pro (11 英寸, 第 3 代)")),
                Scope("全部设备", 1, Item("iPhone 17 Pro")));
            Require(result.Status == UuControllerStatus.Available && result.Names.SequenceEqual(["iPad Pro (11 英寸, 第 3 代)"])
                && result.ObservedAt == observed, "Account device-list name contaminated the local controller snapshot.");
        });

        Test("An all-devices iPad never overrides a local PC or authorizes phone fallback", () =>
        {
            var result = Parse(Scope("全部设备", 1, Item("iPad Pro (11 英寸, 第 3 代)")),
                Scope("本机被控中", 1, Item("DESKTOP-MY-PC")));
            Require(result.Status == UuControllerStatus.Available && result.Names.SequenceEqual(["DESKTOP-MY-PC"]),
                "PC controller was replaced with another device from the account list.");
            var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 1206, 2622), new(0, 0, 1206, 2550), 1.5, true)
            { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
            var legacy = new PhoneProfile { PortraitWidth = 1206, PortraitHeight = 2622 };
            Require(DeviceSessionPolicy.Resolve(result, uu, new(1206, 2622, 144, 32, 1), legacy, observed).Profile is null,
                "Reliable local PC evidence fell back to phone pixels.");
        });

        Test("Device-list names without a verified local scope remain unknown", () =>
        {
            var result = Parse(Scope("全部设备", 1, Item("iPad Pro (11 英寸, 第 3 代)")),
                Node("controllerNameLabel", "iPhone 17 Pro"));
            Require(result.Status == UuControllerStatus.Unknown && result.Names.Count == 0,
                "Names elsewhere in the window authorized a local controller.");
        });

        Test("Multiple controllers or duplicate local panels are ambiguous even when names match", () =>
        {
            Require(Parse(Scope("本机被控中", 2, Item("iPhone 17 Pro"), Item("iPhone 17 Pro"))).Status == UuControllerStatus.Ambiguous,
                "Two same-model controllers were deduplicated into one session.");
            Require(Parse(Scope("本机被控中", 1, Item("iPhone 17 Pro")), Scope("本机被控中", 1, Item("iPhone 17 Pro"))).Status == UuControllerStatus.Ambiguous,
                "Duplicate local panels were silently selected.");
        });

        Test("Only an explicit consistent zero count establishes no local controller", () =>
        {
            Require(Parse(Scope("本机被控中", 0)).Status == UuControllerStatus.None, "Verified empty local scope was not None.");
            Require(Parse(Scope("本机被控中", null)).Status == UuControllerStatus.Unknown, "Missing count was mistaken for disconnection.");
            Require(Parse(Scope("本机被控中", 1)).Status == UuControllerStatus.Unknown, "Half-updated nonzero list was mistaken for disconnection.");
            Require(Parse(Scope("本机被控中", 0, Item("iPhone 17 Pro"))).Status == UuControllerStatus.Unknown,
                "Contradictory count and controller item were trusted.");
        });

        Test("Incomplete or duplicate controller-name labels cannot produce a trusted name", () =>
        {
            Require(Parse(Scope("本机被控中", 1, Node("ControllerInfoView.ControllerItemWidget"))).Status == UuControllerStatus.Unknown,
                "Missing name label was treated as a controller name.");
            Require(Parse(Scope("本机被控中", 1, Item("iPhone\n17 Pro"))).Status == UuControllerStatus.Unknown,
                "Control characters in a name were accepted.");
            var duplicate = Node("ControllerInfoView.ControllerItemWidget", "",
                Node("controllerNameLabel", "iPhone 17 Pro"), Node("controllerNameLabel", "iPhone 17 Pro"));
            Require(Parse(Scope("本机被控中", 1, duplicate)).Status == UuControllerStatus.Ambiguous,
                "Duplicate name labels were silently resolved.");
        });

        Test("Lookalike root and field identifiers cannot expand the trusted scope", () =>
        {
            var local = Scope("本机被控中", 1, Item("iPhone 17 Pro"));
            Require(UuControllerService.ParseTree(Node("OtherHomePageMainWindow", "", local), observed).Status == UuControllerStatus.Unknown,
                "A substring-matching root was accepted.");
            var lookalike = Node("ControllerInfoView.ControllerItemWidget", "",
                Node("ControllerInfoView.ControllerItemWidget.controllerNameLabelBackup", "iPhone 17 Pro"));
            Require(Parse(Scope("本机被控中", 1, lookalike)).Status == UuControllerStatus.Unknown,
                "A lookalike label identifier was accepted.");
        });

        Test("Qualified observed AutomationIds work without trusting arbitrary class substrings", () =>
        {
            var item = Item("iPhone 17 Pro") with { ClassName = "UIControllerItemWidget" };
            var scope = Scope("本机被控中", 1, item) with { AutomationId = "HomePageMainWindow.ControllerInfoView", ClassName = "UIControllerInfoView" };
            Require(Parse(scope).Status == UuControllerStatus.Available, "Real qualified AutomationIds were rejected.");
            Require(Parse(scope with { AutomationId = "unrelated", ClassName = "FakeControllerInfoView" }).Status == UuControllerStatus.Unknown,
                "Arbitrary class substring expanded the trusted scope.");
        });

        Test("Oversized or malformed controller trees fail closed without traversing forever", () =>
        {
            var oversized = Node("HomePageMainWindow", "", Enumerable.Range(0, 513).Select(i => Node("unused" + i)).ToArray());
            Require(UuControllerService.ParseTree(oversized, observed).Status == UuControllerStatus.Unknown,
                "Oversized UI tree was accepted as reliable evidence.");
            Require(UuControllerService.ParseTree(new UuControllerNode("", "HomePageMainWindow", "", null!), observed).Status == UuControllerStatus.Unknown,
                "A structurally invalid tree was trusted.");
        });

        Test("Local-controller headers and items cannot leak across nested scope boundaries", () =>
        {
            var nestedLocal = Scope("本机被控中", 1, Item("iPad Pro (11 英寸, 第 3 代)"));
            var outerAccount = Scope("全部设备", 1, Item("iPhone 17 Pro"), nestedLocal);
            var result = Parse(outerAccount);
            Require(result.Status == UuControllerStatus.Available && result.Names.SequenceEqual(["iPad Pro (11 英寸, 第 3 代)"]),
                "An account scope borrowed a nested local scope header or controller name.");
            var localPc = Scope("本机被控中", 1, Item("DESKTOP-PC"), Scope("全部设备", 1, Item("iPad Pro (11 英寸, 第 3 代)")));
            result = Parse(localPc);
            Require(result.Status == UuControllerStatus.Available && result.Names.SequenceEqual(["DESKTOP-PC"]),
                "A local panel read controller items from a nested account scope.");
            var falseHeader = Node("ControllerInfoView", "", Node("ControllerInfoView.ControllerItemWidget", "",
                Node("titleLabel", "本机被控中"), Node("controllerNameLabel", "iPhone 17 Pro")));
            Require(Parse(falseHeader).Status == UuControllerStatus.Unknown,
                "A controller item's own title was promoted into a local-scope header.");
        });
    }
}
