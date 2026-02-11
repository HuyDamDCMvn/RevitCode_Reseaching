namespace CommonFeature.Models
{
    /// <summary>
    /// Data model for element information display.
    /// </summary>
    public class ElementInfo
    {
        public long Id { get; set; }
        public string FamilyName { get; set; }
        public string FamilyType { get; set; }
        public string Category { get; set; }
        public string Workset { get; set; }
        public string CreatedBy { get; set; }
        public string EditedBy { get; set; }

        public ElementInfo() { }

        public ElementInfo(long id, string familyName, string familyType, string category, string workset, string createdBy, string editedBy)
        {
            Id = id;
            FamilyName = familyName ?? "-";
            FamilyType = familyType ?? "-";
            Category = category ?? "-";
            Workset = workset ?? "-";
            CreatedBy = createdBy ?? "-";
            EditedBy = editedBy ?? "-";
        }
    }
}
