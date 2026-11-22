namespace SharpS3.Models;

using System;

/// <summary>
/// Boundary conditions for certain operations, e.g. PutObject, GetObject, etc.
/// </summary>
public class OperationLimitsSettings
{
        #region Public-Members

        /// <summary>
        /// Maximum content-length for object write (PutObject) before use of multi-part upload is required.
        /// </summary>
        public long MaxPutObjectSize
        {
            get
            {
                return _MaxPutObjectSize;
            }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(MaxPutObjectSize));
                else _MaxPutObjectSize = value;
            }
        }

        /// <summary>
        /// Maximum content-length for uploading a single part (UploadPart).
        /// Default is 5 GB (AWS S3 maximum part size).
        /// </summary>
        public long MaxUploadPartSize
        {
            get
            {
                return _MaxUploadPartSize;
            }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(MaxUploadPartSize));
                else _MaxUploadPartSize = value;
            }
        }

        /// <summary>
        /// Maximum allowed size (in bytes) for XML and control request bodies processed in memory (e.g. CompleteMultipartUpload, DeleteMultiple, ACL, Tagging, Policy, etc.).
        /// Default is 10 MB.
        /// </summary>
        public long MaxXmlPayloadSize
        {
            get
            {
                return _MaxXmlPayloadSize;
            }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(MaxXmlPayloadSize));
                else _MaxXmlPayloadSize = value;
            }
        }

        #endregion

        #region Private-Members

        private long _MaxPutObjectSize = (1024 * 1024 * 128); // 128MB
        private long _MaxUploadPartSize = (5L * 1024 * 1024 * 1024); // 5GB
        private long _MaxXmlPayloadSize = (10 * 1024 * 1024); // 10MB

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiates the object.
        /// </summary>
        public OperationLimitsSettings()
        {

        }

        #endregion

        #region Public-Methods

        #endregion

        #region Private-Methods

        #endregion
    }
