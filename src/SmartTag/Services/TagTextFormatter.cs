using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;

namespace SmartTag.Services
{
    /// <summary>
    /// Service for extracting system information from MEP elements and formatting tag text.
    /// </summary>
    public class TagTextFormatter
    {
        private readonly Document _doc;

        public TagTextFormatter(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        #region System Info Extraction

        /// <summary>
        /// Extract MEP system information from an element.
        /// </summary>
        public MepSystemInfo ExtractSystemInfo(Element element)
        {
            var info = new MepSystemInfo();

            if (element == null) return info;

            // Get category
            info.CategoryName = element.Category?.Name;
            info.BuiltInCategory = GetBuiltInCategory(element);

            // Get family/type names
            info.FamilyName = GetFamilyName(element);
            info.TypeName = GetTypeName(element);

            // Extract based on element type
            switch (element)
            {
                case Pipe pipe:
                    ExtractPipeInfo(pipe, info);
                    break;
                case Duct duct:
                    ExtractDuctInfo(duct, info);
                    break;
                case CableTray cableTray:
                    ExtractCableTrayInfo(cableTray, info);
                    break;
                case Conduit conduit:
                    ExtractConduitInfo(conduit, info);
                    break;
                case FamilyInstance fi:
                    ExtractFamilyInstanceInfo(fi, info);
                    break;
                default:
                    ExtractGenericInfo(element, info);
                    break;
            }

            return info;
        }

        private void ExtractPipeInfo(Pipe pipe, MepSystemInfo info)
        {
            info.ElementType = "Pipe";
            
            // Get pipe system
            var pipingSystem = pipe.MEPSystem as PipingSystem;
            if (pipingSystem != null)
            {
                info.SystemName = pipingSystem.Name;
                info.SystemTypeName = pipingSystem.SystemType.ToString();
                info.SystemClassification = GetPipeSystemClassification(pipingSystem.SystemType);
            }

            // Get size (diameter)
            var diameterParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diameterParam != null && diameterParam.HasValue)
            {
                // Convert from feet to mm
                info.SizeMM = Math.Round(diameterParam.AsDouble() * 304.8);
                info.SizeString = $"DN{info.SizeMM:0}";
            }

            // Get elevation
            var levelOffsetParam = pipe.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
            if (levelOffsetParam != null && levelOffsetParam.HasValue)
            {
                // Convert from feet to meters
                info.ElevationM = Math.Round(levelOffsetParam.AsDouble() * 0.3048, 2);
            }

            // Try to get slope for drainage
            var slopeParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE);
            if (slopeParam != null && slopeParam.HasValue)
            {
                info.Slope = slopeParam.AsDouble(); // Already in decimal (0.01 = 1%)
            }
        }

        private void ExtractDuctInfo(Duct duct, MepSystemInfo info)
        {
            info.ElementType = "Duct";

            // Get duct system
            var ductSystem = duct.MEPSystem as MechanicalSystem;
            if (ductSystem != null)
            {
                info.SystemName = ductSystem.Name;
                info.SystemTypeName = ductSystem.SystemType.ToString();
                info.SystemClassification = GetDuctSystemClassification(ductSystem.SystemType);
            }

            // Get size - could be round or rectangular
            var diameterParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (diameterParam != null && diameterParam.HasValue)
            {
                // Round duct
                info.SizeMM = Math.Round(diameterParam.AsDouble() * 304.8);
                info.SizeString = $"DN{info.SizeMM:0}";
                info.IsRound = true;
            }
            else
            {
                // Rectangular duct
                var widthParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                var heightParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                
                if (widthParam != null && heightParam != null)
                {
                    var widthMM = Math.Round(widthParam.AsDouble() * 304.8);
                    var heightMM = Math.Round(heightParam.AsDouble() * 304.8);
                    info.WidthMM = widthMM;
                    info.HeightMM = heightMM;
                    info.SizeString = $"{widthMM:0}x{heightMM:0}";
                    info.IsRound = false;
                }
            }

            // Get elevation
            var levelOffsetParam = duct.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
            if (levelOffsetParam != null && levelOffsetParam.HasValue)
            {
                info.ElevationM = Math.Round(levelOffsetParam.AsDouble() * 0.3048, 2);
            }
        }

        private void ExtractCableTrayInfo(CableTray cableTray, MepSystemInfo info)
        {
            info.ElementType = "CableTray";

            // Get system from parameter
            var systemParam = cableTray.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
            if (systemParam != null && systemParam.HasValue)
            {
                info.SystemName = systemParam.AsString();
            }

            // Get dimensions
            var widthParam = cableTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var heightParam = cableTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);

            if (widthParam != null && heightParam != null)
            {
                var widthMM = Math.Round(widthParam.AsDouble() * 304.8);
                var heightMM = Math.Round(heightParam.AsDouble() * 304.8);
                info.WidthMM = widthMM;
                info.HeightMM = heightMM;
                info.SizeString = $"{widthMM:0}x{heightMM:0}";
            }

            // Get elevation
            var levelOffsetParam = cableTray.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
            if (levelOffsetParam != null && levelOffsetParam.HasValue)
            {
                info.ElevationM = Math.Round(levelOffsetParam.AsDouble() * 0.3048, 2);
            }
        }

        private void ExtractConduitInfo(Conduit conduit, MepSystemInfo info)
        {
            info.ElementType = "Conduit";

            // Get diameter
            var diameterParam = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (diameterParam != null && diameterParam.HasValue)
            {
                info.SizeMM = Math.Round(diameterParam.AsDouble() * 304.8);
                info.SizeString = $"DN{info.SizeMM:0}";
            }
        }

        private void ExtractFamilyInstanceInfo(FamilyInstance fi, MepSystemInfo info)
        {
            info.ElementType = "Equipment";

            // Try to get connected system
            var connectors = fi.MEPModel?.ConnectorManager?.Connectors;
            if (connectors != null)
            {
                foreach (Connector conn in connectors)
                {
                    if (conn.MEPSystem != null)
                    {
                        info.SystemName = conn.MEPSystem.Name;
                        
                        if (conn.MEPSystem is PipingSystem ps)
                        {
                            info.SystemClassification = GetPipeSystemClassification(ps.SystemType);
                        }
                        else if (conn.MEPSystem is MechanicalSystem ms)
                        {
                            info.SystemClassification = GetDuctSystemClassification(ms.SystemType);
                        }
                        break;
                    }
                }
            }

            // Get dimensions from parameters
            var lengthParam = fi.LookupParameter("Length") ?? fi.LookupParameter("Länge");
            var widthParam = fi.LookupParameter("Width") ?? fi.LookupParameter("Breite");
            var heightParam = fi.LookupParameter("Height") ?? fi.LookupParameter("Höhe");

            if (lengthParam != null) info.LengthMM = Math.Round(lengthParam.AsDouble() * 304.8);
            if (widthParam != null) info.WidthMM = Math.Round(widthParam.AsDouble() * 304.8);
            if (heightParam != null) info.HeightMM = Math.Round(heightParam.AsDouble() * 304.8);

            // Get capacity parameters (heating/cooling)
            var heatingParam = fi.LookupParameter("Heating Capacity") ?? fi.LookupParameter("Heizleistung");
            var coolingParam = fi.LookupParameter("Cooling Capacity") ?? fi.LookupParameter("Kühlleistung");
            var flowParam = fi.LookupParameter("Flow") ?? fi.LookupParameter("Volumenstrom");

            if (heatingParam != null) info.HeatingCapacityW = heatingParam.AsDouble();
            if (coolingParam != null) info.CoolingCapacityW = coolingParam.AsDouble();
            if (flowParam != null) info.FlowRateM3H = flowParam.AsDouble() * 101.94; // Convert CFM to m³/h
        }

        private void ExtractGenericInfo(Element element, MepSystemInfo info)
        {
            // Try to get system name from parameter
            var systemParam = element.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
            if (systemParam != null && systemParam.HasValue)
            {
                info.SystemName = systemParam.AsString();
            }

            // Try to get size parameter
            var sizeParam = element.LookupParameter("Size") ?? element.LookupParameter("Größe");
            if (sizeParam != null && sizeParam.HasValue)
            {
                if (sizeParam.StorageType == StorageType.String)
                {
                    info.SizeString = sizeParam.AsString();
                }
                else if (sizeParam.StorageType == StorageType.Double)
                {
                    info.SizeMM = Math.Round(sizeParam.AsDouble() * 304.8);
                    info.SizeString = $"DN{info.SizeMM:0}";
                }
            }
        }

        private string GetPipeSystemClassification(PipeSystemType systemType)
        {
            // Note: Some enum values vary by Revit version, using string comparison as fallback
            var typeName = systemType.ToString();
            
            return typeName switch
            {
                "DomesticColdWater" => "DomesticColdWater",
                "DomesticHotWater" => "DomesticHotWater",
                "Sanitary" => "SanitaryWaste",
                "Vent" => "Vent",
                "RainWater" or "Storm" => "Storm",
                "Hydronic" or "HydronicSupply" or "HydronicReturn" => "Hydronic",
                "OtherPipe" or "UndefinedSystemType" => "Other",
                // Revit 2025 specific types
                "CondensateDrain" => "Condensate",
                "FireProtectionWet" or "FireProtectionDry" or "FireProtectionPreaction" or "FireProtectionOther" => "FireProtection",
                "ReturnHydronic" => "HydronicReturn",
                "SupplyHydronic" => "HydronicSupply",
                _ => typeName
            };
        }

        private string GetDuctSystemClassification(DuctSystemType systemType)
        {
            return systemType switch
            {
                DuctSystemType.SupplyAir => "SupplyAir",
                DuctSystemType.ReturnAir => "ReturnAir",
                DuctSystemType.ExhaustAir => "ExhaustAir",
                DuctSystemType.OtherAir => "OtherAir",
                _ => systemType.ToString()
            };
        }

        private BuiltInCategory GetBuiltInCategory(Element element)
        {
            try
            {
                return (BuiltInCategory)element.Category.Id.Value;
            }
            catch
            {
                return BuiltInCategory.INVALID;
            }
        }

        private string GetFamilyName(Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol?.Family != null)
            {
                return fi.Symbol.Family.Name;
            }

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = _doc.GetElement(typeId);
                var familyParam = elemType?.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                if (familyParam != null && familyParam.HasValue)
                {
                    return familyParam.AsString();
                }
            }

            return elem.GetType().Name;
        }

        private string GetTypeName(Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol != null)
            {
                return fi.Symbol.Name;
            }

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = _doc.GetElement(typeId);
                if (elemType != null)
                {
                    return elemType.Name;
                }
            }

            return elem.Name ?? "-";
        }

        #endregion

        #region Tag Text Formatting

        /// <summary>
        /// Format tag text using a pattern and system info.
        /// </summary>
        /// <param name="pattern">Tag pattern with placeholders like [System], [Size], [value]</param>
        /// <param name="info">MEP system information</param>
        public string FormatTagText(string pattern, MepSystemInfo info)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

            var result = pattern;

            // Replace common placeholders
            result = ReplacePlaceholder(result, "[System]", info.SystemName ?? info.SystemClassification);
            result = ReplacePlaceholder(result, "[SystemCode]", info.SystemName ?? info.SystemClassification);
            result = ReplacePlaceholder(result, "[Size]", info.SizeMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[DN]", info.SizeMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[Width]", info.WidthMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[Height]", info.HeightMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[Length]", info.LengthMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[L]", info.LengthMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[W]", info.WidthMM?.ToString("0"));
            result = ReplacePlaceholder(result, "[H]", info.HeightMM?.ToString("0"));
            
            // Capacities
            result = ReplacePlaceholder(result, "[Heizleistung]", info.HeatingCapacityW?.ToString("0"));
            result = ReplacePlaceholder(result, "[Kühlleistung]", info.CoolingCapacityW?.ToString("0"));
            result = ReplacePlaceholder(result, "[HeatingCapacity]", info.HeatingCapacityW?.ToString("0"));
            result = ReplacePlaceholder(result, "[CoolingCapacity]", info.CoolingCapacityW?.ToString("0"));
            result = ReplacePlaceholder(result, "[Flow]", info.FlowRateM3H?.ToString("0"));
            result = ReplacePlaceholder(result, "[Value]", info.FlowRateM3H?.ToString("0"));

            // Type/Family
            result = ReplacePlaceholder(result, "[Type]", info.TypeName);
            result = ReplacePlaceholder(result, "[Family]", info.FamilyName);

            return result;
        }

        /// <summary>
        /// Format elevation tag text using rule configuration.
        /// </summary>
        /// <param name="elevationType">RA (centerline) or RS (invert)</param>
        /// <param name="datum">Reference datum (UKRD, ±0.00, OKRFB)</param>
        /// <param name="elevationM">Elevation value in meters</param>
        public string FormatElevationTag(string elevationType, string datum, double elevationM)
        {
            var sign = elevationM >= 0 ? "+" : "";
            
            return datum switch
            {
                "UKRD" => $"{elevationType} = {sign}{elevationM:F2} m UKRD",
                "bez. ±0.00" => $"{elevationType} = {sign}{elevationM:F2} m (bez. ±0.00)",
                "OKRFB" => $"{elevationType} = {sign}{elevationM:F2} OKRFB",
                _ => $"{elevationType} = {sign}{elevationM:F2} m"
            };
        }

        /// <summary>
        /// Format slope annotation.
        /// </summary>
        public string FormatSlopeTag(double slopeDecimal)
        {
            var percent = slopeDecimal * 100;
            return $"{percent:F1}%";
        }

        private string ReplacePlaceholder(string text, string placeholder, string value)
        {
            if (string.IsNullOrEmpty(value)) return text;
            return text.Replace(placeholder, value, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Rule-Based Tag Generation

        /// <summary>
        /// Generate complete tag text for an element using rule engine.
        /// </summary>
        public TagTextResult GenerateTagText(Element element)
        {
            var result = new TagTextResult();
            
            // Extract system info
            var info = ExtractSystemInfo(element);
            result.SystemInfo = info;

            // Get best matching rule
            var ruleEngine = RuleEngine.Instance;
            ruleEngine.Initialize();

            var viewType = "FloorPlan"; // TODO: Get from actual view
            var rule = ruleEngine.GetBestTaggingRule(
                info.BuiltInCategory.ToString(),
                info.FamilyName,
                viewType,
                info.SystemClassification,
                info.SystemName);

            if (rule == null)
            {
                result.MainText = info.SizeString ?? info.TypeName;
                result.RuleUsed = null;
                return result;
            }

            result.RuleUsed = rule.Id;

            // Get tag pattern
            var pattern = ruleEngine.GetTagPattern(rule, "pipeTag");
            if (pattern == null)
            {
                pattern = ruleEngine.GetTagPattern(rule, "pattern");
            }

            if (pattern != null)
            {
                result.MainText = FormatTagText(pattern, info);
            }
            else
            {
                // Fallback to basic format
                result.MainText = $"{info.SystemName ?? info.SystemClassification}_DN{info.SizeMM:0}";
            }

            // Get elevation tag
            var (elevType, datum, _) = ruleEngine.GetElevationReference(rule);
            if (elevType != null && info.ElevationM.HasValue)
            {
                result.ElevationText = FormatElevationTag(elevType, datum, info.ElevationM.Value);
            }

            // Get slope if applicable (drainage)
            if (info.Slope.HasValue && info.Slope.Value > 0)
            {
                result.SlopeText = FormatSlopeTag(info.Slope.Value);
            }

            return result;
        }

        #endregion
    }

    #region Data Classes

    /// <summary>
    /// MEP system information extracted from an element.
    /// </summary>
    public class MepSystemInfo
    {
        // Identity
        public string CategoryName { get; set; }
        public BuiltInCategory BuiltInCategory { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string ElementType { get; set; }

        // System
        public string SystemName { get; set; }
        public string SystemTypeName { get; set; }
        public string SystemClassification { get; set; }

        // Size
        public double? SizeMM { get; set; }
        public double? WidthMM { get; set; }
        public double? HeightMM { get; set; }
        public double? LengthMM { get; set; }
        public string SizeString { get; set; }
        public bool IsRound { get; set; }

        // Position
        public double? ElevationM { get; set; }
        public double? Slope { get; set; }

        // Capacity (equipment)
        public double? HeatingCapacityW { get; set; }
        public double? CoolingCapacityW { get; set; }
        public double? FlowRateM3H { get; set; }
    }

    /// <summary>
    /// Result of tag text generation.
    /// </summary>
    public class TagTextResult
    {
        /// <summary>
        /// Main tag text (e.g., "SW_DN100")
        /// </summary>
        public string MainText { get; set; }

        /// <summary>
        /// Elevation tag text (e.g., "RA = -0.17 m UKRD")
        /// </summary>
        public string ElevationText { get; set; }

        /// <summary>
        /// Slope annotation (e.g., "1.0%")
        /// </summary>
        public string SlopeText { get; set; }

        /// <summary>
        /// Combined multi-line tag text
        /// </summary>
        public string CombinedText
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(MainText)) parts.Add(MainText);
                if (!string.IsNullOrEmpty(ElevationText)) parts.Add(ElevationText);
                return string.Join("\n", parts);
            }
        }

        /// <summary>
        /// ID of the rule used to generate this text
        /// </summary>
        public string RuleUsed { get; set; }

        /// <summary>
        /// Extracted system information
        /// </summary>
        public MepSystemInfo SystemInfo { get; set; }
    }

    #endregion
}
