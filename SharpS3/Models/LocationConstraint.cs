namespace SharpS3.Models
{
    using System.Xml.Serialization;

    /// <summary>
    /// Location constraint for a resource.
    /// </summary>
    [XmlRoot(ElementName = "LocationConstraint")]
    public class LocationConstraint
    {
        #region Public-Members

        /// <summary>
        /// Text, i.e. the region.
        /// Valid values are valid S3 regions, i.e. us-east-1, us-west-1, etc.
        /// </summary>
        [XmlText]
        public string Text
        {
            get => _Text;
            set => _Text = value ?? string.Empty;
        }

        #endregion

        #region Private-Members

        private string _Text = "us-east-1";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public LocationConstraint()
        {
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="region">Region. Valid values are valid S3 regions, i.e. us-west-1.</param>
        public LocationConstraint(string? region)
        {
            Text = region!;
        }

        #endregion
    }
}
