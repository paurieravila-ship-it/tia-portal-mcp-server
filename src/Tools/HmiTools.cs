using System.Linq;
using Newtonsoft.Json.Linq;
using TiaMcpServer.Mcp;

namespace TiaMcpServer.Tools
{
    /// <summary>WinCC Unified HMI: screens, items, bindings, scripts, HMI tags, alarms. Plus classic screen XML.</summary>
    internal static class HmiTools
    {
        public const string Group = "hmi";

        private const string Device = "HMI name (optional: defaults to the first WinCC Unified HMI)";
        private const string Screen = "Screen name";
        private const string ItemOrScreen = "Screen item name, or \"@screen\" for the screen itself";
        private const string Color = "Colour: a .NET name (Red, LightGray, MidnightBlue), #RRGGBB or \"r,g,b\"";
        private const string Overwrite = "Delete and recreate it if it already exists (default false: an existing one is left untouched)";

        private static bool AnyOverwrite(Args a, string arrayName) =>
            (a.Arr(arrayName) ?? new JArray()).OfType<JObject>().Any(o => new Args(o).Bool("overwrite"));

        public static void Register(ToolRegistry r, TiaService tia)
        {
            // ---- Screens --------------------------------------------------------

            r.Add("tia_list_hmi_screens", Group, Safety.ReadOnly, "List HMI screens",
                    "Screens of an HMI: for WinCC Unified with group, size, item count and the runtime start screen; " +
                    "for classic panels the names.")
                .Str("device", "HMI name (default: first Unified HMI, else first classic HMI)")
                .Run(a => tia.ListHmiScreens(a.Str("device")));

            r.Add("tia_hmi_get_screen", Group, Safety.ReadOnly, "Read a screen",
                    "Everything on one WinCC Unified screen: size and background, and each item with type, bounds, " +
                    "text, tag bindings and scripts. Read this before editing a screen. On screens with hundreds of " +
                    "items narrow it with itemFilter, or drop the scripts with withLogic:false.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("itemFilter", "Only items whose name or type contains this text")
                .Bool("withLogic", "Include tag bindings and event scripts (default true)")
                .Run(a => tia.GetHmiScreen(a.Str("device"), a.ReqStr("screen"), a.Str("itemFilter"), a.Bool("withLogic", true)));

            r.Add("tia_hmi_get_item", Group, Safety.ReadOnly, "Read a screen item",
                    "All properties of one screen item (or of the screen) with their current values, plus its " +
                    "bindings and event scripts. Shows what tia_hmi_set_widget_property can change.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Run(a => tia.GetHmiItem(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item")));

            r.Add("tia_hmi_create_screen", Group, Safety.Write, "Create a screen",
                    "Create a WinCC Unified screen. With overwrite:true an existing screen of that name is deleted " +
                    "with everything on it and recreated empty.")
                .Str("device", Device)
                .Str("name", "Screen name", required: true)
                .Int("width", "Width in px (default: the panel's)")
                .Int("height", "Height in px (default: the panel's)")
                .Str("group", "Screen group to create it in, e.g. \"Manual\" (created if missing)")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiScreen(a.Str("device"), a.ReqStr("name"), a.UInt("width", 0), a.UInt("height", 0),
                                              a.Str("group"), a.Bool("overwrite")));

            r.Add("tia_hmi_delete_screen", Group, Safety.Destructive, "Delete a screen",
                    "Delete a WinCC Unified screen and every item, binding and script on it.")
                .Str("device", Device)
                .Str("screen", "Name of the screen to delete", required: true)
                .Bool("removeEmptyGroups", "Also delete the screen group(s) this leaves empty (default false)")
                .Run(a => tia.DeleteHmiScreen(a.Str("device"), a.ReqStr("screen"), a.Bool("removeEmptyGroups")));

            r.Add("tia_hmi_set_start_screen", Group, Safety.Write, "Set the start screen",
                    "Set which screen the WinCC Unified runtime shows at startup. Returns the previous one.")
                .Str("device", Device)
                .Str("screen", "Screen to use as the start screen", required: true)
                .Idempotent()
                .Run(a => tia.SetHmiStartScreen(a.Str("device"), a.ReqStr("screen")));

            r.Add("tia_hmi_set_screen_background", Group, Safety.Write, "Set a screen's background",
                    "Set a screen's background colour(s) and fill pattern (solid or gradient).")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("backColor", "Primary " + Color, required: true)
                .Str("alternateColor", "Second colour for gradients")
                .Str("fillPattern", "Solid | Transparent | GradientHorizontal | GradientVertical | GradientForwardDiagonal | GradientBackwardDiagonal | ...")
                .Idempotent()
                .Run(a => tia.SetHmiScreenBackground(a.Str("device"), a.ReqStr("screen"), a.ReqStr("backColor"),
                                                     a.Str("alternateColor"), a.Str("fillPattern")));

            r.Add("tia_hmi_validate", Group, Safety.ReadOnly, "Validate screens",
                    "Run TIA's validation over one WinCC Unified screen (or all) and return the errors and warnings " +
                    "per item, e.g. bindings to tags that do not exist.")
                .Str("device", Device)
                .Str("screen", "Screen to check (default: all screens)")
                .Run(a => tia.ValidateHmi(a.Str("device"), a.Str("screen")));

            // ---- Items: create --------------------------------------------------

            r.Add("tia_hmi_create_items", Group, Safety.Write, "Create screen items (batch)",
                    "Build several items on a WinCC Unified screen in one call: type, bounds, text, properties, tag " +
                    "bindings and event scripts. The efficient way to lay out a screen. One undo step; if an item " +
                    "fails the whole call is rolled back. Items that already exist are left untouched unless that " +
                    "item sets overwrite:true (which deletes and recreates it).")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Raw("items", new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Items to create",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["type"] = Schema.String("Item type: HmiButton, HmiIOField, HmiText, HmiRectangle, HmiCircle, HmiToggleSwitch, ... (see tia_debug_screen_item_creation_infos)"),
                            ["name"] = Schema.String("Item name, unique on the screen"),
                            ["left"] = new JObject { ["type"] = "integer" },
                            ["top"] = new JObject { ["type"] = "integer" },
                            ["width"] = new JObject { ["type"] = "integer" },
                            ["height"] = new JObject { ["type"] = "integer" },
                            ["text"] = Schema.String("Caption / label text (items that have a Text)"),
                            ["properties"] = new JObject { ["type"] = "object", ["description"] = "Property path -> value, e.g. {\"BackColor\":\"#1E90FF\",\"Font.Size\":18,\"IOFieldType\":\"Output\"}" },
                            ["tagBindings"] = new JObject
                            {
                                ["type"] = "array",
                                ["description"] = "Bind properties to HMI tags. With falseColor+trueColor the Bool tag selects a colour",
                                ["items"] = Schema.Object(
                                    ("property", "string", "Property to bind, e.g. ProcessValue, BackColor, Visible", true),
                                    ("tag", "string", "HMI tag name", true),
                                    ("readOnly", "boolean", "Display only, never write the tag", false),
                                    ("falseColor", "string", "Colour when the tag is false", false),
                                    ("trueColor", "string", "Colour when the tag is true", false))
                            },
                            ["events"] = new JObject
                            {
                                ["type"] = "array",
                                ["description"] = "Event scripts",
                                ["items"] = Schema.Object(
                                    ("event", "string", "Event name, e.g. Tapped, Down, Up", true),
                                    ("script", "string", "JavaScript body of the handler", true))
                            },
                            ["containedType"] = Schema.String("Faceplate containers only: the faceplate type"),
                            ["overwrite"] = new JObject { ["type"] = "boolean", ["description"] = Overwrite }
                        },
                        ["required"] = new JArray("type", "name")
                    }
                }, required: true)
                .DestructiveIf(a => AnyOverwrite(a, "items"), "an item sets overwrite:true")
                .Run(a => tia.CreateHmiItems(a.Str("device"), a.ReqStr("screen"), a.ReqArr("items")));

            r.Add("tia_hmi_create_button", Group, Safety.Write, "Create a button",
                    "Create a button on a WinCC Unified screen. Add behaviour with tia_hmi_set_event_script.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("name", "Button name", required: true)
                .Str("text", "Caption", required: true)
                .Int("left", "X in px")
                .Int("top", "Y in px")
                .Int("width", "Width in px (default 150)")
                .Int("height", "Height in px (default 60)")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiButton(a.Str("device"), a.ReqStr("screen"), a.ReqStr("name"), a.ReqStr("text"),
                                              a.Int("left", 0), a.Int("top", 0), a.UInt("width", 150), a.UInt("height", 60),
                                              a.Bool("overwrite")));

            r.Add("tia_hmi_create_label", Group, Safety.Write, "Create a text label",
                    "Create a static text (title, caption) on a WinCC Unified screen.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("name", "Label name", required: true)
                .Str("text", "Text", required: true)
                .Int("left", "X in px")
                .Int("top", "Y in px")
                .Int("width", "Width in px (default 300)")
                .Int("height", "Height in px (default 60)")
                .Num("fontSize", "Font size in points")
                .Str("fontWeight", "Light | Normal | SemiBold | Bold")
                .Str("foreColor", "Text " + Color)
                .Str("horizontalAlign", "Left | Center | Right | Stretch")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiLabel(a.Str("device"), a.ReqStr("screen"), a.ReqStr("name"), a.ReqStr("text"),
                                             a.Int("left", 0), a.Int("top", 0), a.UInt("width", 300), a.UInt("height", 60),
                                             (float)(a.NumN("fontSize") ?? 0), a.Str("fontWeight"), a.Str("foreColor"),
                                             a.Str("horizontalAlign"), a.Bool("overwrite")));

            r.Add("tia_hmi_create_widget", Group, Safety.Write, "Create any screen item",
                    "Create a screen item of any creatable type (HmiIOField, HmiRectangle, HmiSlider, HmiGauge, " +
                    "HmiSymbolicIOField, ...). Then configure it with tia_hmi_set_widget_property and the binding tools.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("typeName", "Item type, e.g. HmiIOField (list them with tia_debug_screen_item_creation_infos)", required: true)
                .Str("name", "Item name", required: true)
                .Int("left", "X in px")
                .Int("top", "Y in px")
                .Int("width", "Width in px (default 150)")
                .Int("height", "Height in px (default 60)")
                .Str("containedType", "Faceplate containers only: the faceplate type")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiWidget(a.Str("device"), a.ReqStr("screen"), a.ReqStr("typeName"), a.ReqStr("name"),
                                              a.Int("left", 0), a.Int("top", 0), a.UInt("width", 150), a.UInt("height", 60),
                                              a.Str("containedType"), a.Bool("overwrite")));

            // ---- Items: modify --------------------------------------------------

            r.Add("tia_hmi_set_widget_property", Group, Safety.Write, "Set an item property",
                    "Set one property of a screen item (or of the screen) by name; the value is converted to the " +
                    "property's type (number, bool, enum name, colour, text). Nested parts use a dotted path: " +
                    "\"Font.Size\", \"Content.ContentMode\". Collection elements use brackets: \"TrendAreas[0]\" takes " +
                    "an existing element by position (index = current count appends a new one, e.g. \"Trends[1]\" " +
                    "when there is one trend), \"TrendAreas[area1].LeftValueAxes[ax].BeginValue\" takes it by name " +
                    "and creates it if missing. Multilingual texts take one language with '@culture': \"Text@en-US\", " +
                    "\"TrendAreas[0].Trends[1].DisplayName@en-US\" (without it every language gets the same text). " +
                    "The property is read back afterwards: 'set' is true only " +
                    "if it now holds the requested value (TIA sometimes ignores an assignment without an error), " +
                    "'value' is what it reads and 'previous' what it was. Discover properties with tia_hmi_get_item.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("property", "Property name or dotted path, e.g. BackColor, Font.Size, IOFieldType, Visible, TrendAreas[a1].Trends[t1].LineColor", required: true)
                .Str("value", "New value as text", required: true)
                .Idempotent()
                .Run(a => tia.SetHmiWidgetProperty(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"),
                                                   a.ReqStr("property"), a.Str("value") ?? ""));

            r.Add("tia_hmi_get_software_object", Group, Safety.ReadOnly, "Read an HMI software object",
                    "Read any object of the WinCC Unified HMI software (not a screen item) by path from the HMI " +
                    "software: \"AlarmClasses\" lists the alarm classes, \"AlarmClasses[Alarm].RaisedState\" shows " +
                    "one state's colours, \"RuntimeSettings\" the runtime settings. Collections list their elements; " +
                    "objects list their simple properties and the names of their parts. Empty path = the software root.")
                .Str("device", Device)
                .Str("path", "Path from the HMI software, e.g. AlarmClasses[Alarm].RaisedState (empty = root)")
                .Run(a => tia.GetHmiSoftwareObject(a.Str("device"), a.Str("path")));

            r.Add("tia_hmi_set_software_property", Group, Safety.Write, "Set an HMI software property",
                    "Set one property of an HMI software object (alarm classes, runtime settings, ...) by path from " +
                    "the HMI software, e.g. \"AlarmClasses[Alarm].RaisedState.BackColor\" = \"#E07B1A\". Same value " +
                    "conversion, collection and '@culture' rules as tia_hmi_set_widget_property; read back afterwards. " +
                    "Discover paths with tia_hmi_get_software_object.")
                .Str("device", Device)
                .Str("property", "Path from the HMI software, e.g. AlarmClasses[Warning].RaisedState.Flashing", required: true)
                .Str("value", "New value as text", required: true)
                .Idempotent()
                .Run(a => tia.SetHmiSoftwareProperty(a.Str("device"), a.ReqStr("property"), a.Str("value") ?? ""));

            r.Add("tia_hmi_set_widget_color", Group, Safety.Write, "Set an item colour",
                    "Set a static colour property (BackColor, ForeColor, BorderColor, ...) of a screen item. For a " +
                    "colour that follows a tag use tia_hmi_set_property_tag_color.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("property", "Colour property, e.g. BackColor", required: true)
                .Str("color", Color, required: true)
                .Idempotent()
                .Run(a => tia.SetHmiWidgetColor(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"),
                                                a.ReqStr("property"), a.ReqStr("color")));

            r.Add("tia_hmi_set_item_bounds", Group, Safety.Write, "Move / resize an item",
                    "Reposition or resize a screen item in place, keeping its bindings and scripts. Only the given " +
                    "values change; returns the bounds before and after.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", "Screen item name", required: true)
                .Int("left", "X in px")
                .Int("top", "Y in px")
                .Int("width", "Width in px")
                .Int("height", "Height in px")
                .Idempotent()
                .Run(a => tia.SetHmiItemBounds(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"),
                                               a.IntN("left"), a.IntN("top"),
                                               a.Has("width") ? a.UInt("width", 0) : (uint?)null,
                                               a.Has("height") ? a.UInt("height", 0) : (uint?)null));

            r.Add("tia_hmi_set_button_text", Group, Safety.Write, "Set a caption",
                    "Change the caption text of a button (or any item with a Text). Returns the previous text.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("button", "Item name", required: true)
                .Str("text", "New caption", required: true)
                .Idempotent()
                .Run(a => tia.SetHmiButtonText(a.Str("device"), a.ReqStr("screen"), a.ReqStr("button"), a.Str("text") ?? ""));

            r.Add("tia_hmi_set_button_graphic", Group, Safety.Write, "Set a button image",
                    "Set a button's image and how image and text are combined. The image must already be in the " +
                    "project's graphics collection (import it in TIA Portal).")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("button", "Button name", required: true)
                .Str("graphic", "Image name as shown in the project graphics", required: true)
                .Str("contentMode", "GraphicOrText | GraphicAndText | Text | Graphic")
                .Idempotent()
                .Run(a => tia.SetHmiButtonGraphic(a.Str("device"), a.ReqStr("screen"), a.ReqStr("button"),
                                                  a.ReqStr("graphic"), a.Str("contentMode")));

            r.Add("tia_hmi_delete_screen_item", Group, Safety.Destructive, "Delete a screen item",
                    "Delete an item from a WinCC Unified screen together with its bindings and scripts. What was " +
                    "removed is returned in the result.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", "Name of the item to delete", required: true)
                .Run(a => tia.DeleteHmiScreenItem(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item")));

            // ---- Scripts and bindings ------------------------------------------

            r.Add("tia_hmi_set_event_script", Group, Safety.Write, "Set an event script",
                    "Attach or replace the JavaScript of an event on any screen item or on the screen (e.g. Tapped, " +
                    "Down, Up on a button; Loaded on a screen) and syntax-check it. Returns the previous script. " +
                    "To write a PLC bit from a script use HMIRuntime.Tags.SysFct.SetBitInTag(\"Tag\", 0) / " +
                    "ResetBitInTag: assigning Tags(\"x\").Value alone does not write.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("eventType", "Event name; an invalid one returns the list valid for that item type", required: true)
                .Str("scriptCode", "JavaScript body of the handler", required: true)
                .Bool("async", "Mark the handler async (needed for await)")
                .Str("globalDefinition", "Code for the script's global definition area (imports, shared variables)")
                .Run(a => tia.SetHmiEventScript(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"), a.ReqStr("eventType"),
                                                a.ReqStr("scriptCode"), a.BoolN("async"), a.Str("globalDefinition")));

            r.Add("tia_hmi_set_button_script", Group, Safety.Write, "Set a button event script",
                    "Same as tia_hmi_set_event_script, kept for buttons: attach or replace a button's event script " +
                    "(Tapped, Down, Up, Activated, Deactivated, KeyDown, KeyUp, ContextTapped) and syntax-check it.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("button", "Button name", required: true)
                .Str("eventType", "Event name, e.g. Down, Up, Tapped", required: true)
                .Str("scriptCode", "JavaScript body of the handler", required: true)
                .Run(a => tia.SetHmiEventScript(a.Str("device"), a.ReqStr("screen"), a.ReqStr("button"), a.ReqStr("eventType"),
                                                a.ReqStr("scriptCode"), null, null));

            r.Add("tia_hmi_remove_event_script", Group, Safety.Destructive, "Remove an event script",
                    "Remove the handler of an event from a screen item or the screen. The removed script is returned.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("eventType", "Event whose handler to remove", required: true)
                .Run(a => tia.RemoveHmiEventScript(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"), a.ReqStr("eventType")));

            r.Add("tia_hmi_bind_property_to_tag", Group, Safety.Write, "Bind a property to a tag",
                    "Bind an item property 1:1 to an HMI tag (native tag dynamization, no converter): ProcessValue " +
                    "of an IO field, Visible, a numeric property... Replaces an existing tag binding on that " +
                    "property and returns the previous tag.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("property", "Property to bind, e.g. ProcessValue", required: true)
                .Str("tag", "HMI tag name", required: true)
                .Bool("readOnly", "Display only, never write the tag (default false)")
                .Idempotent()
                .Run(a => tia.BindHmiPropertyToTag(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"),
                                                   a.ReqStr("property"), a.ReqStr("tag"), a.Bool("readOnly")));

            r.Add("tia_hmi_set_property_tag_color", Group, Safety.Write, "Colour a property by a Bool tag",
                    "Make a colour property (e.g. BackColor) follow a Bool HMI tag: one colour when false, another " +
                    "when true. Native tag dynamization with a two-entry mapping table - the reliable way to show " +
                    "a feedback state.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("property", "Colour property, e.g. BackColor", required: true)
                .Str("tag", "Bool HMI tag name", required: true)
                .Str("falseColor", "When the tag is false. " + Color, required: true)
                .Str("trueColor", "When the tag is true. " + Color, required: true)
                .Idempotent()
                .Run(a => tia.SetHmiPropertyTagColor(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"), a.ReqStr("property"),
                                                     a.ReqStr("tag"), a.ReqStr("falseColor"), a.ReqStr("trueColor")));

            r.Add("tia_hmi_set_property_script", Group, Safety.Write, "Script a property",
                    "Attach or replace a script that computes an item property (script dynamization) and " +
                    "syntax-check it. Returns the previous script. Prefer the native tag bindings when a tag is enough.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("property", "Property to compute, e.g. BackColor", required: true)
                .Str("scriptCode", "JavaScript that returns the property value", required: true)
                .Str("trigger", "When to re-run: AutomaticTags | T100ms | T250ms | T500ms | T1s | T2s | T5s | T10s | Disabled")
                .Run(a => tia.SetHmiPropertyScript(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"), a.ReqStr("property"),
                                                   a.ReqStr("scriptCode"), a.Str("trigger")));

            r.Add("tia_hmi_remove_property_dynamization", Group, Safety.Destructive, "Remove a property binding",
                    "Remove the tag binding or script from an item property; the property returns to its static " +
                    "value. What was removed is returned.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Str("item", ItemOrScreen, required: true)
                .Str("property", "Property whose dynamization to remove", required: true)
                .Run(a => tia.RemoveHmiPropertyDynamization(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"), a.ReqStr("property")));

            // ---- HMI tags and connections --------------------------------------

            r.Add("tia_hmi_list_tags", Group, Safety.ReadOnly, "List HMI tags",
                    "HMI tags of a WinCC Unified HMI with connection, PLC tag, data type and acquisition cycle; also " +
                    "the tag tables and connection names.")
                .Str("device", Device)
                .Str("table", "Only this HMI tag table (default: all)")
                .Str("filter", "Only tags whose name or PLC tag contains this text")
                .Run(a => tia.ListHmiTags(a.Str("device"), a.Str("table"), a.Str("filter")));

            r.Add("tia_hmi_create_tag_table", Group, Safety.Write, "Create an HMI tag table",
                    "Create an empty HMI tag table. An existing one is left untouched.")
                .Str("device", Device)
                .Str("name", "HMI tag table name", required: true)
                .Idempotent()
                .Run(a => tia.CreateHmiTagTable(a.Str("device"), a.ReqStr("name")));

            r.Add("tia_hmi_create_tags", Group, Safety.Write, "Create HMI tags (batch)",
                    "Create several HMI tags. With connection + plcTag a tag is linked to the PLC; with neither it " +
                    "is an HMI-internal tag. Existing tags are left untouched unless that tag sets overwrite:true " +
                    "(which deletes and recreates it).")
                .Str("device", Device)
                .Str("table", "HMI tag table for the tags (default: the first table)")
                .Arr("tags", "Tags to create", Schema.Object(
                    ("name", "string", "HMI tag name", true),
                    ("connection", "string", "HMI connection name (see tia_hmi_list_tags). With plcTag: links to the PLC", false),
                    ("plcTag", "string", "PLC tag or DB member, e.g. Start_Button or \"DB_HMI\".Speed", false),
                    ("dataType", "string", "Data type (internal tags: default Bool; PLC tags take the PLC tag's type)", false),
                    ("acquisitionCycle", "string", "Polling cycle, e.g. T100ms, T1s", false),
                    ("table", "string", "Tag table for this tag (overrides the call's table)", false),
                    ("overwrite", "boolean", Overwrite, false)), required: true)
                .DestructiveIf(a => AnyOverwrite(a, "tags"), "a tag sets overwrite:true")
                .Run(a => tia.CreateHmiTags(a.Str("device"), a.Str("table"), a.ReqArr("tags")));

            r.Add("tia_hmi_create_tag", Group, Safety.Write, "Create an HMI tag (PLC)",
                    "Create one HMI tag linked to a PLC tag through an HMI connection.")
                .Str("device", Device)
                .Str("name", "HMI tag name", required: true)
                .Str("connection", "HMI connection name (see tia_hmi_list_tags)", required: true)
                .Str("plcTag", "PLC tag or DB member, e.g. Start_Button or \"DB_HMI\".Speed", required: true)
                .Str("dataType", "HMI tag data type (default: taken from the PLC tag)")
                .Str("table", "HMI tag table (default: the first table)")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiTag(a.Str("device"), a.ReqStr("name"), a.ReqStr("connection"), a.ReqStr("plcTag"),
                                           a.Str("dataType"), a.Str("table"), a.Bool("overwrite")));

            r.Add("tia_hmi_create_internal_tag", Group, Safety.Write, "Create an internal HMI tag",
                    "Create an HMI-internal tag (no PLC connection): panel-local memory, e.g. to hold the selected " +
                    "page or a last-commanded state.")
                .Str("device", Device)
                .Str("name", "HMI tag name", required: true)
                .Str("dataType", "Data type (default Bool)")
                .Str("table", "HMI tag table (default: the first table)")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiTag(a.Str("device"), a.ReqStr("name"), null, null,
                                           a.Str("dataType"), a.Str("table"), a.Bool("overwrite")));

            r.Add("tia_hmi_delete_tag", Group, Safety.Destructive, "Delete an HMI tag",
                    "Delete an HMI tag. Screen bindings and scripts that use it are left broken; check " +
                    "tia_hmi_find_tag_usage first.")
                .Str("device", Device)
                .Str("tag", "Name of the HMI tag to delete", required: true)
                .Run(a => tia.DeleteHmiTag(a.Str("device"), a.ReqStr("tag")));

            r.Add("tia_hmi_set_tag_acquisition_cycle", Group, Safety.Write, "Set a tag's polling cycle",
                    "Set how often the HMI reads a PLC-linked tag, e.g. T100ms, T500ms, T1s. Internal tags have no cycle.")
                .Str("device", Device)
                .Str("tag", "HMI tag name", required: true)
                .Str("cycle", "Cycle, e.g. T100ms, T1s", required: true)
                .Idempotent()
                .Run(a => tia.SetHmiTagAcquisitionCycle(a.Str("device"), a.ReqStr("tag"), a.ReqStr("cycle")));

            r.Add("tia_hmi_set_connection_enabled", Group, Safety.Write, "Enable / disable an HMI connection",
                    "Enable or disable an HMI connection at runtime start. Tags through a disabled connection do not " +
                    "update on the panel.")
                .Str("device", Device)
                .Str("connection", "HMI connection name", required: true)
                .Bool("enabled", "true = active at startup", required: true)
                .Idempotent()
                .Run(a => tia.SetHmiConnectionEnabled(a.Str("device"), a.ReqStr("connection"), a.ReqBool("enabled")));

            // ---- Tag usage and faceplates --------------------------------------

            r.Add("tia_hmi_find_tag_usage", Group, Safety.ReadOnly, "Find where HMI tags are used",
                    "List the tag references on WinCC Unified screens: tag bindings and faceplate interface " +
                    "parameters; with a filter also scripts that mention it. Use before renaming or deleting an HMI tag.")
                .Str("device", Device)
                .Str("screen", "Only this screen (default: all screens)")
                .Str("filter", "Only references containing this text (also searches scripts)")
                .Run(a => tia.FindHmiTagUsage(a.Str("device"), a.Str("screen"), a.Str("filter")));

            r.Add("tia_hmi_replace_tag_refs", Group, Safety.Write, "Replace tag references (regex)",
                    "Regex find/replace over tag bindings and faceplate interface parameters, on one screen or all. " +
                    "dryRun (the default) only lists what would change. Applying it (dryRun:false) rewrites many " +
                    "bindings at once and needs confirm:true. Scripts are not touched.")
                .Str("device", Device)
                .Str("screen", "Only this screen (default: all screens)")
                .Str("pattern", "Regular expression matched against the bound tag name (case-insensitive)", required: true)
                .Str("replacement", "Replacement; $1, $2 refer to capture groups", required: true)
                .Bool("dryRun", "List the changes without applying them (default true)")
                .DestructiveIf(a => !a.Bool("dryRun", true), "dryRun:false")
                .Run(a => tia.ReplaceHmiTagRefs(a.Str("device"), a.Str("screen"), a.ReqStr("pattern"),
                                                a.Str("replacement") ?? "", a.Bool("dryRun", true)));

            r.Add("tia_hmi_update_faceplate_interface", Group, Safety.Write, "Set faceplate parameters",
                    "Set interface parameters of faceplate instances on a screen, i.e. point a faceplate at other " +
                    "tags. Returns the previous values.")
                .Str("device", Device)
                .Str("screen", Screen, required: true)
                .Arr("updates", "Parameters to set", Schema.Object(
                    ("container", "string", "Faceplate container name on the screen", true),
                    ("parameter", "string", "Interface parameter name", true),
                    ("value", "string", "New value, usually a tag name", true)), required: true)
                .Run(a => tia.UpdateFaceplateInterface(a.Str("device"), a.ReqStr("screen"), a.ReqArr("updates")));

            // ---- Alarms ---------------------------------------------------------

            r.Add("tia_hmi_list_alarms", Group, Safety.ReadOnly, "List HMI alarms",
                    "Alarm classes, discrete alarms and analog alarms of a WinCC Unified HMI.")
                .Str("device", Device)
                .Run(a => tia.ListHmiAlarms(a.Str("device")));

            r.Add("tia_hmi_create_alarm", Group, Safety.Write, "Create an HMI alarm",
                    "Create a discrete alarm (raised by one bit of an HMI tag) or an analog alarm (raised when an " +
                    "HMI tag crosses a limit). The HMI tag must exist.")
                .Str("device", Device)
                .Str("kind", "discrete (default) | analog")
                .Str("name", "Alarm name", required: true)
                .Str("tag", "HMI tag that raises the alarm", required: true)
                .Int("bit", "discrete: bit number within the tag (default 0)")
                .Str("text", "Alarm text shown to the operator")
                .Str("alarmClass", "Alarm class name (see tia_hmi_list_alarms; default: TIA's default class)")
                .Int("priority", "Priority 0-16")
                .Str("triggerMode", "discrete: OnRisingEdge (default) | OnFallingEdge")
                .Str("condition", "analog: LowerLimit | UpperLimit | Equal | NotEqual | LowerLimitOrEqual | UpperLimitOrEqual")
                .Str("conditionValue", "analog: the limit value")
                .Bool("overwrite", Overwrite)
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.CreateHmiAlarm(a.Str("device"), a.Str("kind"), a.ReqStr("name"), a.ReqStr("tag"), a.IntN("bit"),
                                             a.Str("alarmClass"), a.Str("text"), a.IntN("priority"), a.Str("triggerMode"),
                                             a.Str("condition"), a.Str("conditionValue"), a.Bool("overwrite")));

            r.Add("tia_hmi_delete_alarm", Group, Safety.Destructive, "Delete an HMI alarm",
                    "Delete a discrete or analog alarm from a WinCC Unified HMI.")
                .Str("device", Device)
                .Str("name", "Name of the alarm to delete", required: true)
                .Run(a => tia.DeleteHmiAlarm(a.Str("device"), a.ReqStr("name")));

            // ---- Classic panels -------------------------------------------------

            r.Add("tia_export_screen", Group, Safety.ReadOnly, "Export a classic screen (XML file)",
                    "Classic panels (Comfort/Basic) only: export a screen to SimaticML XML. Does not modify the " +
                    "project; overwrites <outDir>/<screen>.xml. WinCC Unified screens are read with tia_hmi_get_screen.")
                .Str("device", "Classic HMI name (default: first classic HMI)")
                .Str("screen", Screen, required: true)
                .Path("outDir", "Output directory", required: true)
                .Run(a => tia.ExportScreen(a.Str("device"), a.ReqStr("screen"), a.ReqStr("outDir")));

            r.Add("tia_import_screen", Group, Safety.Write, "Import a classic screen (XML)",
                    "Classic panels (Comfort/Basic) only: create a screen from SimaticML XML. With overwrite:true " +
                    "an existing screen of the same name is replaced.")
                .Str("device", "Classic HMI name (default: first classic HMI)")
                .Path("xmlPath", "Absolute path to the screen XML", required: true)
                .Bool("overwrite", "Replace the screen if it already exists (default false)")
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.ImportScreen(a.Str("device"), a.ReqStr("xmlPath"), a.Bool("overwrite")));
        }
    }
}
