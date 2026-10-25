using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Tag;
using Siemens.Engineering.HmiUnified.UI.Enum;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.Shapes;
using Siemens.Engineering.HmiUnified.UI.Widgets;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    // WinCC Unified screens: items, properties, dynamizations and event scripts.
    internal sealed partial class TiaService
    {
        /// <summary>Item name that addresses the screen itself in tools that take an item.</summary>
        private const string ScreenSelf = "@screen";

        // ---- Lookup ------------------------------------------------------------

        private static IEnumerable<HmiScreen> AllUnifiedScreens(HmiSoftware hmi)
        {
            foreach (HmiScreen s in hmi.Screens) yield return s;
            foreach (HmiScreenGroup g in hmi.ScreenGroups)
                foreach (var s in ScreensInGroup(g))
                    yield return s;
        }

        private static IEnumerable<HmiScreen> ScreensInGroup(HmiScreenGroup group)
        {
            foreach (HmiScreen s in group.Screens) yield return s;
            foreach (HmiScreenGroup sub in group.Groups)
                foreach (var s in ScreensInGroup(sub))
                    yield return s;
        }

        private static string ScreenGroupPath(HmiScreen screen)
        {
            var parts = new List<string>();
            var parent = screen.Parent;
            while (parent is HmiScreenGroup g)
            {
                parts.Insert(0, g.Name);
                parent = g.Parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>Finds a screen anywhere, including inside screen groups.</summary>
        private static HmiScreen RequireUnifiedScreen(HmiSoftware hmi, string name)
        {
            var screen = AllUnifiedScreens(hmi)
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (screen == null)
                throw new InvalidOperationException(
                    $"Screen '{name}' not found on '{hmi.Name}'. Use tia_list_hmi_screens to see the screen names.");
            return screen;
        }

        private static HmiScreenItemBase RequireScreenItem(HmiScreen screen, string itemName)
        {
            var item = screen.ScreenItems.Find(itemName);
            if (item == null)
                throw new InvalidOperationException(
                    $"Screen item '{itemName}' not found on '{screen.Name}'. Use tia_hmi_get_screen to list the items.");
            return item;
        }

        /// <summary>The screen item, or the screen itself for "@screen" / an empty name.</summary>
        private static object ResolveTarget(HmiScreen screen, string itemName) =>
            string.IsNullOrEmpty(itemName) || itemName == ScreenSelf
                ? (object)screen
                : RequireScreenItem(screen, itemName);

        private static DynamizationBaseComposition DynamizationsOf(object target) =>
            target is HmiScreen s ? s.Dynamizations : ((HmiScreenItemBase)target).Dynamizations;

        private static string TargetName(object target) =>
            target is HmiScreen s ? ScreenSelf + " (" + s.Name + ")" : ((HmiScreenItemBase)target).Name;

        // ---- Text and colour conversion ---------------------------------------

        /// <summary>Unified stores display texts as HTML; plain text is wrapped accordingly.</summary>
        private static void SetMultilingualText(MultilingualText mlText, string text)
        {
            text = text ?? "";
            var html = text.TrimStart().StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                ? text
                : $"<body><p>{System.Net.WebUtility.HtmlEncode(text)}</p></body>";
            foreach (MultilingualTextItem item in mlText.Items)
                item.Text = html;
        }

        private static string PlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;
            return System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();
        }

        /// <summary>
        /// Accepts a .NET colour name (Red, LightGray), #RRGGBB, #AARRGGBB or "r,g,b".
        /// Unknown names are rejected: Color.FromName would silently yield transparent black.
        /// </summary>
        private static Color ParseColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Colour value is empty.");
            var s = value.Trim();

            var hex = s.StartsWith("#") ? s.Substring(1) : s;
            if ((hex.Length == 6 || hex.Length == 8) && hex.All(Uri.IsHexDigit))
            {
                if (hex.Length == 6) hex = "FF" + hex;
                return Color.FromArgb(unchecked((int)Convert.ToUInt32(hex, 16)));
            }

            var rgb = s.Split(',');
            if (rgb.Length == 3 && rgb.All(p => byte.TryParse(p.Trim(), out _)))
                return Color.FromArgb(byte.Parse(rgb[0].Trim()), byte.Parse(rgb[1].Trim()), byte.Parse(rgb[2].Trim()));

            var named = Color.FromName(s);
            if (!named.IsKnownColor)
                throw new ArgumentException(
                    $"Unknown colour '{value}'. Use a .NET colour name (e.g. Red, LightGray, MidnightBlue), #RRGGBB or \"r,g,b\".");
            return named;
        }

        private static string ColorHex(Color c) =>
            c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Property value in a JSON-friendly form.</summary>
        private static object FormatValue(object value)
        {
            if (value == null) return null;
            if (value is Color c) return ColorHex(c);
            if (value is System.Enum) return value.ToString();
            if (value is MultilingualText ml) return PlainText(FirstText(ml));
            if (value is string || value.GetType().IsPrimitive || value is decimal) return value;
            return value.ToString();
        }

        private static object ConvertValue(string value, Type type, string propertyName)
        {
            try
            {
                if (type == typeof(string) || type == typeof(object)) return value;
                if (type.IsEnum)
                {
                    try { return System.Enum.Parse(type, value, true); }
                    catch
                    {
                        throw new ArgumentException(
                            $"'{value}' is not valid for '{propertyName}'. Valid values: {string.Join(", ", System.Enum.GetNames(type))}.");
                    }
                }
                if (type == typeof(bool)) return bool.Parse(value);
                if (type == typeof(Color)) return ParseColor(value);
                return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            }
            catch (ArgumentException) { throw; }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    $"Cannot convert '{value}' to {type.Name} for property '{propertyName}': {ex.Message}");
            }
        }

        private static string TokenToString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JValue v) return Convert.ToString(v.Value, CultureInfo.InvariantCulture);
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---- Reflection over items --------------------------------------------

        private static object GetProp(object obj, string name) =>
            Try(() => obj.GetType().GetProperty(name)?.GetValue(obj));

        /// <summary>Outcome of setting a property: what it was, what it reads now, and whether that is what was asked.</summary>
        private sealed class PropertyChange
        {
            public object Previous;
            public object Actual;
            public bool Applied;
        }

        private static bool SameValue(object wanted, object actual)
        {
            if (wanted == null || actual == null) return wanted == null && actual == null;
            if (wanted is Color a && actual is Color b) return a.ToArgb() == b.ToArgb();
            if (wanted is float || wanted is double)
            {
                var w = Convert.ToDouble(wanted, CultureInfo.InvariantCulture);
                var r = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                return Math.Abs(w - r) <= 1e-6 * Math.Max(1.0, Math.Abs(w));
            }
            return Equals(wanted, actual) ||
                   string.Equals(Convert.ToString(wanted, CultureInfo.InvariantCulture),
                                 Convert.ToString(actual, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private const string NotAppliedNote =
            "TIA Portal accepted the call without an error but the property does not read back as the requested " +
            "value, so it was NOT applied. The property may be fixed by another setting of the item, or need a " +
            "different value format.";

        /// <summary>
        /// Sets a property addressed by a dotted path ("BackColor", "Font.Size",
        /// "Content.ContentMode"). Multilingual text properties (Text, ToolTipText) take
        /// plain text. Openness sometimes ignores an assignment without raising an error,
        /// so the property is read back and the result says whether the value took.
        /// </summary>
        private static PropertyChange SetPropertyPath(object target, string path, string value)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Property name is empty.");

            var parts = path.Split('.');
            var current = target;
            for (int i = 0; i < parts.Length - 1; i++)
                current = StepInto(current, parts[i], path);

            var last = parts[parts.Length - 1];
            // "Text@en-US": write only that language of a multilingual text
            string culture = null;
            var at = last.IndexOf('@');
            if (at > 0) { culture = last.Substring(at + 1).Trim(); last = last.Substring(0, at); }
            var prop = current.GetType().GetProperty(last);
            if (prop == null)
                throw new InvalidOperationException(
                    $"'{current.GetType().Name}' has no property '{last}'. Use tia_hmi_get_item to list the properties.");

            if (culture != null)
            {
                if (!typeof(MultilingualText).IsAssignableFrom(prop.PropertyType))
                    throw new InvalidOperationException($"'{last}' is not a multilingual text; '@{culture}' does not apply.");
                var items = ((MultilingualText)prop.GetValue(current)).Items.Cast<MultilingualTextItem>().ToList();
                var item = items.FirstOrDefault(i =>
                               string.Equals(i.Language?.Culture?.Name, culture, StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException(
                               $"Language '{culture}' is not in the project. Available: " +
                               string.Join(", ", items.Select(i => i.Language?.Culture?.Name)));
                var before = PlainText(item.Text);
                var html = (value ?? "").TrimStart().StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                    ? value
                    : $"<body><p>{System.Net.WebUtility.HtmlEncode(value ?? "")}</p></body>";
                item.Text = html;
                var after = PlainText(item.Text) ?? "";
                return new PropertyChange { Previous = before, Actual = after, Applied = after == PlainText(html) };
            }

            if (typeof(MultilingualText).IsAssignableFrom(prop.PropertyType))
            {
                var ml = (MultilingualText)prop.GetValue(current);
                var previousText = PlainText(FirstText(ml));
                SetMultilingualText(ml, value);

                var wantedText = (value ?? "").TrimStart().StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                    ? PlainText(value)
                    : (value ?? "").Trim();
                var actualText = PlainText(FirstText(ml)) ?? "";
                return new PropertyChange { Previous = previousText, Actual = actualText, Applied = actualText == wantedText };
            }

            if (!prop.CanWrite)
                throw new InvalidOperationException($"Property '{path}' of '{current.GetType().Name}' is read-only.");

            var previous = Try(() => FormatValue(prop.GetValue(current)));
            var converted = ConvertValue(value, prop.PropertyType, path);
            prop.SetValue(current, converted);

            var readBack = prop.GetValue(current);
            return new PropertyChange { Previous = previous, Actual = FormatValue(readBack), Applied = SameValue(converted, readBack) };
        }

        private static readonly Regex CollectionStep = new Regex(@"^(\w+)\[([^\]]+)\]$");

        /// <summary>
        /// One step of a property path. "Font" reads a part; "TrendAreas[0]" takes an existing
        /// element of a composition by position ("Trends[n]" with n = Count appends one when the
        /// composition has a parameterless Create()); "TrendAreas[area1]" takes the element with that
        /// name and creates it when the composition has Create(string) and it does not exist yet
        /// (trend areas, trends, value / time axes ...).
        /// </summary>
        private static object StepInto(object current, string part, string path, bool create = true)
        {
            var m = CollectionStep.Match(part);
            var propName = m.Success ? m.Groups[1].Value : part;
            var next = current.GetType().GetProperty(propName)?.GetValue(current);
            if (next == null)
                throw new InvalidOperationException(
                    $"'{current.GetType().Name}' has no readable part '{propName}' (in '{path}').");
            if (!m.Success) return next;

            var key = m.Groups[2].Value.Trim();
            var items = (next as IEnumerable)?.Cast<object>().ToList()
                ?? throw new InvalidOperationException($"'{propName}' is not a collection (in '{path}').");

            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                // One past the end appends, for compositions whose Create() takes no name (trends)
                var append = next.GetType().GetMethod("Create", Type.EmptyTypes);
                if (create && index == items.Count && append != null)
                {
                    try { return append.Invoke(next, null); }
                    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                }
                if (index < 0 || index >= items.Count)
                    throw new InvalidOperationException(
                        $"'{propName}' has {items.Count} element(s); index {index} does not exist (in '{path}'). " +
                        "Address a new element by name, e.g. " + propName + "[myName], to create it.");
                return items[index];
            }

            var found = items.FirstOrDefault(o =>
                string.Equals(o.GetType().GetProperty("Name")?.GetValue(o) as string, key, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
            if (!create)
                throw new InvalidOperationException(
                    $"No element '{key}' in '{propName}' (in '{path}'). Elements: " +
                    string.Join(", ", items.Select(o => o.GetType().GetProperty("Name")?.GetValue(o) as string ?? "?")));

            var createByName = next.GetType().GetMethod("Create", new[] { typeof(string) })
                ?? throw new InvalidOperationException(
                    $"No element '{key}' in '{propName}', and the collection cannot create one by name (in '{path}').");
            try { return createByName.Invoke(next, new object[] { key }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        /// <summary>All simple readable properties of an item; "parts" (Font, Content, ...) one level deep.</summary>
        private static Dictionary<string, object> ReadProperties(object obj, int depth)
        {
            var skip = new HashSet<string> { "Parent", "Dynamizations", "EventHandlers", "PropertyEventHandlers", "CurrentQuality" };
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);

            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || skip.Contains(p.Name)) continue;
                var t = p.PropertyType;

                object value;
                try { value = p.GetValue(obj); }
                catch { continue; }

                if (t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(Color) ||
                    typeof(MultilingualText).IsAssignableFrom(t))
                {
                    result[p.Name] = FormatValue(value);
                }
                else if (depth < 1 && value != null && t.Namespace != null &&
                         t.Namespace.EndsWith(".UI.Parts") && !(value is IEnumerable))
                {
                    var nested = ReadProperties(value, depth + 1);
                    if (nested.Count > 0) result[p.Name] = nested;
                }
            }

            return result.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        private static object DescribeDynamization(DynamizationBase d)
        {
            var o = new Dictionary<string, object>
            {
                ["property"] = d.PropertyName,
                ["type"] = d.DynamizationType.ToString()
            };
            if (d is TagDynamization td)
            {
                o["tag"] = td.Tag;
                if (td.ReadOnly) o["readOnly"] = true;
            }
            if (d is ScriptDynamization sd)
            {
                o["script"] = sd.ScriptCode;
                o["trigger"] = Try(() => sd.Trigger.Type.ToString());
            }
            return o;
        }

        private static List<object> DescribeEvents(object target)
        {
            var list = new List<object>();
            if (!(GetProp(target, "EventHandlers") is IEnumerable handlers)) return list;
            foreach (var handler in handlers)
            {
                var script = GetProp(handler, "Script") as IHmiScript;
                list.Add(new
                {
                    @event = GetProp(handler, "EventType")?.ToString(),
                    script = Try(() => script?.ScriptCode)
                });
            }
            return list;
        }

        private static Dictionary<string, object> DescribeItem(HmiScreenItemBase item, bool withLogic)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = item.Name,
                ["type"] = item.GetType().Name
            };

            foreach (var p in new[] { "Left", "Top", "Width", "Height" })
            {
                var v = GetProp(item, p);
                if (v != null) d[p.ToLowerInvariant()] = v;
            }

            if (GetProp(item, "Text") is MultilingualText text)
                d["text"] = PlainText(FirstText(text));

            if (withLogic)
            {
                var dyn = item.Dynamizations.Select(DescribeDynamization).ToArray();
                if (dyn.Length > 0) d["dynamizations"] = dyn;
                var events = DescribeEvents(item);
                if (events.Count > 0) d["events"] = events;
            }
            return d;
        }

        // ---- Screens -----------------------------------------------------------

        /// <summary>Screens of a WinCC Unified panel (with groups and sizes) or of a classic panel.</summary>
        public object ListHmiScreens(string deviceName)
        {
            var unified = FindSoftware<HmiSoftware>(deviceName, h => h.Name)?.Software;
            if (unified != null)
            {
                var screens = AllUnifiedScreens(unified).Select(s => new
                {
                    name = s.Name,
                    group = NullIfEmpty(ScreenGroupPath(s)),
                    width = s.Width,
                    height = s.Height,
                    items = s.ScreenItems.Count
                }).ToArray();
                return new
                {
                    device = unified.Name,
                    kind = "hmiUnified",
                    startScreen = Try(() => unified.RuntimeSettings.StartScreen),
                    count = screens.Length,
                    screens
                };
            }

            var classic = RequireHmi(deviceName);
            var classicScreens = AllScreens(classic.ScreenFolder).Select(s => new { name = s.Name }).ToArray();
            return new { device = classic.Name, kind = "hmiClassic", count = classicScreens.Length, screens = classicScreens };
        }

        /// <summary>One screen in full: its settings and every item with bounds, text, bindings and scripts.</summary>
        public object GetHmiScreen(string deviceName, string screenName, string itemFilter, bool withLogic)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            var items = screen.ScreenItems
                .Where(i => string.IsNullOrEmpty(itemFilter) ||
                            i.Name.IndexOf(itemFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            i.GetType().Name.IndexOf(itemFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(i => DescribeItem(i, withLogic)).ToArray();

            return new
            {
                screen = screen.Name,
                group = NullIfEmpty(ScreenGroupPath(screen)),
                width = screen.Width,
                height = screen.Height,
                backColor = ColorHex(screen.BackColor),
                isStartScreen = string.Equals(Try(() => hmi.RuntimeSettings.StartScreen), screen.Name, StringComparison.OrdinalIgnoreCase),
                dynamizations = screen.Dynamizations.Select(DescribeDynamization).ToArray(),
                events = DescribeEvents(screen),
                itemCount = screen.ScreenItems.Count,
                itemsShown = items.Length,
                items
            };
        }

        /// <summary>Every simple property of a screen item (or of the screen for "@screen").</summary>
        public object GetHmiItem(string deviceName, string screenName, string itemName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            return new
            {
                screen = screen.Name,
                item = TargetName(target),
                type = target.GetType().Name,
                properties = ReadProperties(target, 0),
                dynamizations = DynamizationsOf(target).Select(DescribeDynamization).ToArray(),
                events = DescribeEvents(target),
                note = "Set any of these with tia_hmi_set_widget_property; nested parts use a dotted path such as \"Font.Size\"."
            };
        }

        public object DumpScreenItemBounds(string deviceName, string screenName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            return new { screen = screen.Name, items = screen.ScreenItems.Select(i => DescribeItem(i, false)).ToArray() };
        }

        public object CreateHmiScreen(string deviceName, string name, uint width, uint height, string group, bool overwrite)
        {
            var hmi = RequireHmiUnified(deviceName);
            var existing = AllUnifiedScreens(hmi).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !overwrite)
                return new { created = false, exists = true, name };

            return Edit($"create screen {name}", () =>
            {
                existing?.Delete();

                HmiScreenComposition target = hmi.Screens;
                foreach (var part in SplitGroupPath(group))
                {
                    // Walk / create the screen-group path.
                    var groups = target.Parent is HmiScreenGroup pg ? pg.Groups : hmi.ScreenGroups;
                    var g = groups.Find(part) ?? groups.Create(part);
                    target = g.Screens;
                }

                var screen = target.Create(name);
                if (width > 0) screen.Width = width;
                if (height > 0) screen.Height = height;
                return new
                {
                    created = true,
                    exists = false,
                    name = screen.Name,
                    group = NullIfEmpty(ScreenGroupPath(screen)),
                    width = screen.Width,
                    height = screen.Height,
                    replacedExisting = existing != null
                };
            });
        }

        public object DeleteHmiScreen(string deviceName, string screenName, bool removeEmptyGroups)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var items = screen.ScreenItems.Count;
            var isStart = string.Equals(Try(() => hmi.RuntimeSettings.StartScreen), screen.Name, StringComparison.OrdinalIgnoreCase);
            var group = screen.Parent as HmiScreenGroup;

            return Edit($"delete screen {screenName}", () =>
            {
                screen.Delete();

                // Optionally tidy up the screen groups this leaves empty, innermost first.
                var removedGroups = new List<string>();
                while (removeEmptyGroups && group != null && group.Screens.Count == 0 && group.Groups.Count == 0)
                {
                    var parent = group.Parent as HmiScreenGroup;
                    removedGroups.Add(group.Name);
                    group.Delete();
                    group = parent;
                }

                return new
                {
                    deleted = true,
                    screen = screenName,
                    itemsRemoved = items,
                    groupsRemoved = removedGroups.Count == 0 ? null : removedGroups,
                    note = isStart ? "This was the runtime start screen; set a new one with tia_hmi_set_start_screen." : null
                };
            });
        }

        public object SetHmiStartScreen(string deviceName, string screenName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var previous = Try(() => hmi.RuntimeSettings.StartScreen);
            return Edit($"set start screen {screenName}", () =>
            {
                hmi.RuntimeSettings.StartScreen = screen.Name;
                return new { set = true, startScreen = hmi.RuntimeSettings.StartScreen, previous };
            });
        }

        public object SetHmiScreenBackground(string deviceName, string screenName,
                                             string backColor, string alternateColor, string fillPattern)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            return Edit($"set background of {screenName}", () =>
            {
                screen.BackColor = ParseColor(backColor);
                if (!string.IsNullOrEmpty(alternateColor))
                    screen.AlternateBackColor = ParseColor(alternateColor);
                if (!string.IsNullOrEmpty(fillPattern))
                    screen.BackFillPattern = (HmiFillPattern)ConvertValue(fillPattern, typeof(HmiFillPattern), "fillPattern");

                return new
                {
                    set = true,
                    screen = screen.Name,
                    backColor = ColorHex(screen.BackColor),
                    alternateBackColor = ColorHex(screen.AlternateBackColor),
                    backFillPattern = screen.BackFillPattern.ToString()
                };
            });
        }

        // ---- Items: create -----------------------------------------------------

        private static readonly Dictionary<string, Type> ScreenItemTypes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolves "HmiButton" / "Button" / a full type name to a creatable screen-item type.</summary>
        private static Type ResolveScreenItemType(HmiScreen screen, string typeName)
        {
            if (ScreenItemTypes.Count == 0)
            {
                foreach (var info in ((IEngineeringObject)screen).GetCreationInfos("ScreenItems"))
                {
                    if (info.Type == null) continue;
                    ScreenItemTypes[info.Type.Name] = info.Type;
                    ScreenItemTypes[info.Type.FullName] = info.Type;
                }
            }

            if (ScreenItemTypes.TryGetValue(typeName, out var type) ||
                ScreenItemTypes.TryGetValue("Hmi" + typeName, out type))
                return type;

            throw new InvalidOperationException(
                $"'{typeName}' is not a creatable screen-item type. " +
                "Use tia_debug_screen_item_creation_infos to list them (e.g. HmiButton, HmiIOField, HmiText, HmiRectangle).");
        }

        private static HmiScreenItemBase CreateScreenItem(HmiScreen screen, string typeName, string name, string containedType)
        {
            var type = ResolveScreenItemType(screen, typeName);
            var paramCount = string.IsNullOrEmpty(containedType) ? 1 : 2;
            var create = screen.ScreenItems.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition &&
                                     m.GetParameters().Length == paramCount &&
                                     m.GetParameters().All(p => p.ParameterType == typeof(string)));
            if (create == null)
                throw new InvalidOperationException("ScreenItems.Create<T> was not found in this Openness version.");

            var args = paramCount == 1 ? new object[] { name } : new object[] { name, containedType };
            var item = create.MakeGenericMethod(type).Invoke(screen.ScreenItems, args) as HmiScreenItemBase;
            if (item == null)
                throw new InvalidOperationException($"Create<{type.Name}>('{name}') returned nothing.");
            return item;
        }

        private static void SetBounds(object item, int? left, int? top, uint? width, uint? height)
        {
            void Set(string prop, object value)
            {
                if (value == null) return;
                var p = item.GetType().GetProperty(prop);
                if (p == null || !p.CanWrite)
                    throw new InvalidOperationException($"'{item.GetType().Name}' has no settable '{prop}'.");
                p.SetValue(item, Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture));
            }
            Set("Left", left);
            Set("Top", top);
            Set("Width", width);
            Set("Height", height);
        }

        public object CreateHmiWidget(string deviceName, string screenName, string typeName, string name,
                                      int left, int top, uint width, uint height, string containedType, bool overwrite)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            var existing = screen.ScreenItems.Find(name);
            if (existing != null && !overwrite)
                return new { created = false, exists = true, name };

            return Edit($"create {typeName} {name}", () =>
            {
                existing?.Delete();
                var item = CreateScreenItem(screen, typeName, name, containedType);
                SetBounds(item, left, top, width, height);
                return new { created = true, exists = false, name, typeName = item.GetType().Name, replacedExisting = existing != null };
            });
        }

        public object CreateHmiLabel(string deviceName, string screenName, string name, string text,
                                     int left, int top, uint width, uint height,
                                     float fontSize, string fontWeight, string foreColor,
                                     string horizontalAlign, bool overwrite)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            var existing = screen.ScreenItems.Find(name);
            if (existing != null && !overwrite)
                return new { created = false, exists = true, name };

            return Edit($"create label {name}", () =>
            {
                existing?.Delete();
                var label = screen.ScreenItems.Create<HmiText>(name);
                label.Left = left;
                label.Top = top;
                label.Width = width;
                label.Height = height;
                SetMultilingualText(label.Text, text);
                if (fontSize > 0) label.Font.Size = fontSize;
                if (!string.IsNullOrEmpty(fontWeight))
                    label.Font.Weight = (HmiFontWeight)ConvertValue(fontWeight, typeof(HmiFontWeight), "fontWeight");
                if (!string.IsNullOrEmpty(foreColor))
                    label.ForeColor = ParseColor(foreColor);
                if (!string.IsNullOrEmpty(horizontalAlign))
                    label.HorizontalTextAlignment =
                        (HmiHorizontalAlignment)ConvertValue(horizontalAlign, typeof(HmiHorizontalAlignment), "horizontalAlign");

                return new { created = true, exists = false, name = label.Name, replacedExisting = existing != null };
            });
        }

        public object CreateHmiButton(string deviceName, string screenName, string name, string text,
                                      int left, int top, uint width, uint height, bool overwrite)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            var existing = screen.ScreenItems.Find(name);
            if (existing != null && !overwrite)
                return new { created = false, exists = true, name };

            return Edit($"create button {name}", () =>
            {
                existing?.Delete();
                var button = screen.ScreenItems.Create<HmiButton>(name);
                button.Left = left;
                button.Top = top;
                button.Width = width;
                button.Height = height;
                SetMultilingualText(button.Text, text);
                return new { created = true, exists = false, name = button.Name, replacedExisting = existing != null };
            });
        }

        /// <summary>
        /// Builds several screen items in one call - type, bounds, text, properties, tag
        /// bindings and event scripts - as a single undo step. One failing item aborts
        /// the whole call.
        /// </summary>
        public object CreateHmiItems(string deviceName, string screenName, JArray items)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            return Edit($"create {items.Count} item(s) on {screenName}", () =>
            {
                var results = new List<object>();
                foreach (var token in items)
                {
                    var spec = new Args(token as JObject);
                    var name = spec.Str("name") ?? "(unnamed)";
                    try
                    {
                        results.Add(CreateOneHmiItem(screen, spec));
                    }
                    catch (Exception ex)
                    {
                        var real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                        throw new InvalidOperationException(
                            $"Item '{name}': {Brief(real.Message)} " +
                            (ServerOptions.Current.Transactions
                                ? "The whole call was rolled back; no items were created."
                                : $"{results.Count} earlier item(s) of this call were already created."));
                    }
                }
                return new { screen = screen.Name, count = results.Count, results };
            });
        }

        private static object CreateOneHmiItem(HmiScreen screen, Args spec)
        {
            var name = spec.ReqStr("name");
            var typeName = spec.ReqStr("type");

            var existing = screen.ScreenItems.Find(name);
            if (existing != null)
            {
                if (!spec.Bool("overwrite"))
                    return new { name, status = "exists", note = "left untouched; pass overwrite:true on the item to replace it" };
                existing.Delete();
            }

            var item = CreateScreenItem(screen, typeName, name, spec.Str("containedType"));
            SetBounds(item, spec.IntN("left"), spec.IntN("top"),
                      spec.Has("width") ? spec.UInt("width", 0) : (uint?)null,
                      spec.Has("height") ? spec.UInt("height", 0) : (uint?)null);

            // Assignments TIA silently ignored are reported per item rather than failing the batch.
            var notApplied = new List<object>();
            void Set(string path, string value)
            {
                var change = SetPropertyPath(item, path, value);
                if (!change.Applied)
                    notApplied.Add(new { property = path, requested = value, reads = change.Actual });
            }

            if (spec.Has("text"))
                Set("Text", spec.Str("text") ?? "");

            var properties = spec.Obj("properties");
            if (properties != null)
                foreach (var p in properties.Properties())
                    Set(p.Name, TokenToString(p.Value));

            foreach (var token in spec.Arr("tagBindings") ?? new JArray())
            {
                var b = new Args(token as JObject);
                var property = b.ReqStr("property");
                var dyn = item.Dynamizations.Create<TagDynamization>(property);
                dyn.Tag = b.ReqStr("tag");

                // Two colours turn the binding into a false/true colour mapping.
                if (b.Has("falseColor") || b.Has("trueColor"))
                    ApplyBoolColorMapping(dyn, b.ReqStr("falseColor"), b.ReqStr("trueColor"));
                else if (b.Has("readOnly"))
                    dyn.ReadOnly = b.Bool("readOnly");
            }

            var scriptIssues = new List<object>();
            foreach (var token in spec.Arr("events") ?? new JArray())
            {
                var e = new Args(token as JObject);
                var outcome = ApplyEventScript(item, e.ReqStr("event"), e.ReqStr("script"), null, null);
                if (outcome.Errors.Length > 0 || outcome.Warnings.Length > 0)
                    scriptIssues.Add(new { @event = outcome.EventName, errors = outcome.Errors, warnings = outcome.Warnings });
            }

            return new
            {
                name,
                status = existing != null ? "replaced" : "created",
                type = item.GetType().Name,
                propertiesNotApplied = notApplied.Count == 0 ? null : notApplied,
                scriptIssues = scriptIssues.Count == 0 ? null : scriptIssues
            };
        }

        // ---- Items: modify / delete -------------------------------------------

        public object DeleteHmiScreenItem(string deviceName, string screenName, string itemName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            var item = screen.ScreenItems.Find(itemName);
            if (item == null)
                return new { deleted = false, exists = false, item = itemName };

            var removed = DescribeItem(item, true);
            return Edit($"delete {itemName}", () =>
            {
                item.Delete();
                return new { deleted = true, exists = true, item = itemName, removed };
            });
        }

        public object SetHmiItemBounds(string deviceName, string screenName, string itemName,
                                       int? left, int? top, uint? width, uint? height)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var item = RequireScreenItem(screen, itemName);

            var before = new { left = GetProp(item, "Left"), top = GetProp(item, "Top"), width = GetProp(item, "Width"), height = GetProp(item, "Height") };
            return Edit($"move/resize {itemName}", () =>
            {
                SetBounds(item, left, top, width, height);
                return new
                {
                    resized = true,
                    item = itemName,
                    before,
                    after = new { left = GetProp(item, "Left"), top = GetProp(item, "Top"), width = GetProp(item, "Width"), height = GetProp(item, "Height") }
                };
            });
        }

        public object SetHmiWidgetProperty(string deviceName, string screenName, string itemName,
                                           string propertyPath, string value)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            return Edit($"set {propertyPath} of {TargetName(target)}", () =>
            {
                var change = SetPropertyPath(target, propertyPath, value);
                return new
                {
                    set = change.Applied,
                    item = TargetName(target),
                    property = propertyPath,
                    requested = value,
                    value = change.Actual,
                    previous = change.Previous,
                    note = change.Applied ? null : NotAppliedNote
                };
            });
        }

        /// <summary>
        /// Reads any object of the Unified HMI software by path from HmiSoftware
        /// ("AlarmClasses", "AlarmClasses[Alarm].RaisedState", "RuntimeSettings"...).
        /// Collections list their elements; objects list their simple properties and
        /// the names of their parts / collections, for further navigation. Never creates.
        /// </summary>
        public object GetHmiSoftwareObject(string deviceName, string path)
        {
            var hmi = RequireHmiUnified(deviceName);
            object current = hmi;
            if (!string.IsNullOrWhiteSpace(path))
                foreach (var part in path.Split('.'))
                    current = StepInto(current, part, path, create: false);

            if (current is IEnumerable seq && !(current is string))
            {
                var elements = seq.Cast<object>().Select((o, i) => new
                {
                    index = i,
                    name = o.GetType().GetProperty("Name")?.GetValue(o) as string,
                    type = o.GetType().Name
                }).ToArray();
                return new { path = path ?? "", type = current.GetType().Name, count = elements.Length, elements };
            }

            var parts = current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "Parent" &&
                            !(p.PropertyType == typeof(string) || p.PropertyType.IsPrimitive || p.PropertyType.IsEnum ||
                              p.PropertyType == typeof(Color) || typeof(MultilingualText).IsAssignableFrom(p.PropertyType)))
                .Select(p => p.Name).OrderBy(n => n).ToArray();
            return new
            {
                path = path ?? "",
                type = current.GetType().Name,
                properties = ReadProperties(current, 0),
                parts,
                note = "Navigate with path segments separated by '.', collections by [index] or [name]. " +
                       "Change values with tia_hmi_set_software_property."
            };
        }

        /// <summary>Sets a property of any Unified HMI software object (alarm classes, runtime settings, ...).</summary>
        public object SetHmiSoftwareProperty(string deviceName, string propertyPath, string value)
        {
            var hmi = RequireHmiUnified(deviceName);
            return Edit($"set {propertyPath} of HMI software", () =>
            {
                var change = SetPropertyPath(hmi, propertyPath, value);
                return new
                {
                    set = change.Applied,
                    property = propertyPath,
                    requested = value,
                    value = change.Actual,
                    previous = change.Previous,
                    note = change.Applied ? null : NotAppliedNote
                };
            });
        }

        public object SetHmiWidgetColor(string deviceName, string screenName, string itemName,
                                        string propertyName, string color)
        {
            ParseColor(color);   // fail early, with the colour-specific message
            return SetHmiWidgetProperty(deviceName, screenName, itemName, propertyName, color);
        }

        public object SetHmiButtonText(string deviceName, string screenName, string buttonName, string text)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var item = RequireScreenItem(screen, buttonName);

            return Edit($"set text of {buttonName}", () =>
            {
                var change = SetPropertyPath(item, "Text", text);
                return new
                {
                    set = change.Applied,
                    button = buttonName,
                    text = change.Actual,
                    previous = change.Previous,
                    note = change.Applied ? null : NotAppliedNote
                };
            });
        }

        public object SetHmiButtonGraphic(string deviceName, string screenName, string buttonName,
                                          string graphicName, string contentMode)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var button = RequireScreenItem(screen, buttonName) as HmiButton;
            if (button == null)
                throw new InvalidOperationException($"'{buttonName}' on '{screenName}' is not a button.");

            return Edit($"set graphic of {buttonName}", () =>
            {
                var previous = button.Graphic;
                button.Graphic = graphicName;
                if (!string.IsNullOrEmpty(contentMode))
                    button.Content.ContentMode = (HmiContentMode)ConvertValue(contentMode, typeof(HmiContentMode), "contentMode");

                return new { set = true, button = buttonName, graphic = button.Graphic, previous, contentMode = button.Content.ContentMode.ToString() };
            });
        }

        // ---- Dynamizations -----------------------------------------------------

        private static void ApplyBoolColorMapping(TagDynamization dyn, string falseColor, string trueColor)
        {
            var mt = dyn.ValueConverter.MappingTable;
            mt.ConditionType = ConditionType.Singlebit;
            var entries = (mt.Entries.Count > 0
                    ? mt.Entries.Cast<MappingTableEntryBase>()
                    : mt.Entries.Create(BitDynamizationType.SingleBit).Cast<MappingTableEntryBase>())
                .OfType<MappingTableEntryBitmask>().ToList();

            var falseEntry = entries.FirstOrDefault(e => e.Condition == 0);
            var trueEntry = entries.FirstOrDefault(e => e.Condition == 1);
            if (falseEntry == null || trueEntry == null)
                throw new InvalidOperationException(
                    $"Expected 2 bitmask entries with Condition 0/1, got: {string.Join(",", entries.Select(e => e.Condition))}");
            falseEntry.Value = ParseColor(falseColor);
            trueEntry.Value = ParseColor(trueColor);
        }

        public object SetHmiPropertyTagColor(string deviceName, string screenName, string itemName,
                                             string propertyName, string tagName,
                                             string falseColor, string trueColor)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);
            ParseColor(falseColor);
            ParseColor(trueColor);

            return Edit($"bind {propertyName} of {TargetName(target)} to {tagName}", () =>
            {
                var dynamizations = DynamizationsOf(target);
                var dyn = dynamizations.Find(propertyName) as TagDynamization
                          ?? dynamizations.Create<TagDynamization>(propertyName);
                dyn.Tag = tagName;
                ApplyBoolColorMapping(dyn, falseColor, trueColor);

                return new { set = true, item = TargetName(target), property = propertyName, tag = tagName };
            });
        }

        public object BindHmiPropertyToTag(string deviceName, string screenName, string itemName,
                                           string propertyName, string tagName, bool readOnly)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            return Edit($"bind {propertyName} of {TargetName(target)} to {tagName}", () =>
            {
                var dynamizations = DynamizationsOf(target);
                var dyn = dynamizations.Find(propertyName) as TagDynamization
                          ?? dynamizations.Create<TagDynamization>(propertyName);
                var previousTag = dyn.Tag;
                dyn.Tag = tagName;
                dyn.ReadOnly = readOnly;

                return new
                {
                    bound = true,
                    item = TargetName(target),
                    property = propertyName,
                    tag = tagName,
                    readOnly,
                    previousTag = NullIfEmpty(previousTag)
                };
            });
        }

        public object SetHmiPropertyScript(string deviceName, string screenName, string itemName,
                                           string propertyName, string scriptCode, string trigger)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            return Edit($"script {propertyName} of {TargetName(target)}", () =>
            {
                var dynamizations = DynamizationsOf(target);
                var dyn = dynamizations.Find(propertyName) as ScriptDynamization
                          ?? dynamizations.Create<ScriptDynamization>(propertyName);
                var previous = dyn.ScriptCode;
                dyn.ScriptCode = scriptCode;
                if (!string.IsNullOrEmpty(trigger))
                    dyn.Trigger.Type = (TriggerType)ConvertValue(trigger, typeof(TriggerType), "trigger");

                var result = dyn.SyntaxCheck();
                return new
                {
                    set = true,
                    item = TargetName(target),
                    property = propertyName,
                    trigger = Try(() => dyn.Trigger.Type.ToString()),
                    errors = result.Errors.Cast<object>().Select(e => e.ToString()).ToArray(),
                    warnings = result.Warnings.Cast<object>().Select(w => w.ToString()).ToArray(),
                    previousScript = NullIfEmpty(previous)
                };
            });
        }

        public object RemoveHmiPropertyDynamization(string deviceName, string screenName, string itemName,
                                                    string propertyName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            var dyn = DynamizationsOf(target).Find(propertyName);
            if (dyn == null)
                return new { removed = false, item = TargetName(target), property = propertyName, note = "No dynamization found." };

            var removed = DescribeDynamization(dyn);
            return Edit($"remove dynamization {propertyName} of {TargetName(target)}", () =>
            {
                dyn.Delete();
                return new { removed = true, item = TargetName(target), property = propertyName, was = removed };
            });
        }

        public object DumpPropertyDynamizationColors(string deviceName, string screenName, string itemName,
                                                      string propertyName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            var dyn = DynamizationsOf(target).Find(propertyName) as TagDynamization;
            if (dyn == null)
                throw new InvalidOperationException(
                    $"No TagDynamization found for property '{propertyName}' on '{TargetName(target)}'.");

            var entries = dyn.ValueConverter.MappingTable.Entries.Select((e, idx) => new
            {
                index = idx,
                entryType = e.GetType().Name,
                condition = (e as MappingTableEntryBitmask)?.Condition,
                valueType = e.Value?.GetType().Name,
                hex = e.Value is Color c ? ColorHex(c) : e.Value?.ToString()
            }).ToArray();

            return new { item = TargetName(target), property = propertyName, tag = dyn.Tag, entries };
        }

        // ---- Event scripts -----------------------------------------------------

        private sealed class EventScriptOutcome
        {
            public string EventName;
            public string PreviousScript;
            public string[] Errors;
            public string[] Warnings;
        }

        /// <summary>
        /// Attaches a script to an event of any item or of the screen. Every widget type
        /// has its own event enum and handler collection, so this goes through reflection.
        /// </summary>
        private static EventScriptOutcome ApplyEventScript(object target, string eventType, string scriptCode,
                                                           bool? isAsync, string globalDefinition)
        {
            var handlers = GetProp(target, "EventHandlers");
            if (handlers == null)
                throw new InvalidOperationException($"'{target.GetType().Name}' has no events.");

            MethodInfo Method(string name) => handlers.GetType().GetMethods().FirstOrDefault(m =>
                m.Name == name && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);

            var find = Method("Find");
            var create = Method("Create");
            if (find == null || create == null)
                throw new InvalidOperationException($"'{target.GetType().Name}' events cannot be scripted through Openness.");

            var enumType = create.GetParameters()[0].ParameterType;
            object evt;
            try { evt = System.Enum.Parse(enumType, eventType, true); }
            catch
            {
                throw new ArgumentException(
                    $"Unknown event '{eventType}' for {target.GetType().Name}. Valid events: " +
                    string.Join(", ", System.Enum.GetNames(enumType).Where(n => n != "None")) + ".");
            }

            var handler = find.Invoke(handlers, new[] { evt }) ?? create.Invoke(handlers, new[] { evt });
            var script = GetProp(handler, "Script") as IHmiScript;
            if (script == null)
                throw new InvalidOperationException($"Event '{evt}' of {target.GetType().Name} exposes no script.");

            var previous = script.ScriptCode;
            script.ScriptCode = scriptCode;
            if (isAsync != null) script.Async = isAsync.Value;
            if (globalDefinition != null) script.GlobalDefinitionAreaScriptCode = globalDefinition;

            var check = script.SyntaxCheck();
            return new EventScriptOutcome
            {
                EventName = evt.ToString(),
                PreviousScript = previous,
                Errors = check.Errors.Cast<object>().Select(e => e.ToString()).ToArray(),
                Warnings = check.Warnings.Cast<object>().Select(w => w.ToString()).ToArray()
            };
        }

        public object SetHmiEventScript(string deviceName, string screenName, string itemName, string eventType,
                                        string scriptCode, bool? isAsync, string globalDefinition)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            return Edit($"script {eventType} of {TargetName(target)}", () =>
            {
                var outcome = ApplyEventScript(target, eventType, scriptCode, isAsync, globalDefinition);
                return new
                {
                    set = true,
                    item = TargetName(target),
                    eventType = outcome.EventName,
                    errors = outcome.Errors,
                    warnings = outcome.Warnings,
                    previousScript = NullIfEmpty(outcome.PreviousScript)
                };
            });
        }

        public object RemoveHmiEventScript(string deviceName, string screenName, string itemName, string eventType)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var target = ResolveTarget(screen, itemName);

            if (!(GetProp(target, "EventHandlers") is IEnumerable handlers))
                throw new InvalidOperationException($"'{target.GetType().Name}' has no events.");

            object match = null;
            foreach (var h in handlers)
                if (string.Equals(GetProp(h, "EventType")?.ToString(), eventType, StringComparison.OrdinalIgnoreCase))
                    match = h;

            if (match == null)
                return new { removed = false, item = TargetName(target), eventType, note = "No handler for that event." };

            var previous = Try(() => (GetProp(match, "Script") as IHmiScript)?.ScriptCode);
            return Edit($"remove {eventType} script of {TargetName(target)}", () =>
            {
                match.GetType().GetMethod("Delete").Invoke(match, null);
                return new { removed = true, item = TargetName(target), eventType, previousScript = previous };
            });
        }

        // ---- Validation --------------------------------------------------------

        /// <summary>Runs TIA's own validation over one screen (or all) and returns only the findings.</summary>
        public object ValidateHmi(string deviceName, string screenName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screens = screenName != null
                ? new List<HmiScreen> { RequireUnifiedScreen(hmi, screenName) }
                : AllUnifiedScreens(hmi).ToList();

            var findings = new List<object>();
            void Collect(string screen, string item, IEnumerable<Siemens.Engineering.HmiUnified.Common.HmiValidationResult> results)
            {
                foreach (var r in results)
                {
                    var errors = r.Errors?.ToArray() ?? new string[0];
                    var warnings = r.Warnings?.ToArray() ?? new string[0];
                    if (errors.Length == 0 && warnings.Length == 0) continue;
                    findings.Add(new { screen, item, property = r.PropertyName, errors, warnings });
                }
            }

            foreach (var screen in screens)
            {
                try { Collect(screen.Name, ScreenSelf, screen.Validate()); }
                catch (Exception ex) { findings.Add(new { screen = screen.Name, item = ScreenSelf, error = Brief(ex.Message) }); }

                foreach (var item in screen.ScreenItems)
                {
                    try { Collect(screen.Name, item.Name, item.Validate()); }
                    catch (Exception ex) { findings.Add(new { screen = screen.Name, item = item.Name, error = Brief(ex.Message) }); }
                }
            }

            return new { device = hmi.Name, screensChecked = screens.Count, findingCount = findings.Count, findings };
        }

        // ---- API discovery (diagnostics) --------------------------------------

        public object DumpScreenItemCreationInfos(string deviceName, string screenName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            var infos = ((IEngineeringObject)screen).GetCreationInfos("ScreenItems");
            return new
            {
                infos = infos.Select(i => new
                {
                    typeName = i.Type?.Name,
                    typeFullName = i.Type?.FullName,
                    parameters = i.ParameterInfos.Select(p => new { p.Name, p.IsMandatory }).ToArray()
                }).ToArray()
            };
        }

        public object DebugScreenItemTypeProperties(string deviceName, string screenName, string typeName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);
            var t = ResolveScreenItemType(screen, typeName);

            return new
            {
                typeName = t.Name,
                typeFullName = t.FullName,
                baseType = t.BaseType?.FullName,
                properties = t.GetProperties()
                    .Select(p => new { p.Name, type = p.PropertyType.FullName, canRead = p.CanRead, canWrite = p.CanWrite })
                    .OrderBy(p => p.Name)
                    .ToArray()
            };
        }
    }
}
