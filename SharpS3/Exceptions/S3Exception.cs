namespace SharpS3.Exceptions;

using SharpS3.Models;
using System;

    /// <summary>
    /// S3 exception.  Throw an instance of this object if an S3 error response needs to be returned to the caller.
    /// </summary>
    public class S3Exception : Exception
    {
        #region Public-Members

        /// <summary>
        /// Error.
        /// </summary>
        public Error? Error { get; set; } = null;

        /// <summary>
        /// Inner exception.
        /// </summary>
        public new Exception? InnerException { get; set; } = null;

        /// <summary>
        /// Status code for the given exception.
        /// </summary>
        public int HttpStatusCode
        {
            get
            {
                if (Error != null) return Error.HttpStatusCode;
                return 500;
            }
        }

        #endregion

        #region Private-Members

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="error">Error.</param>
        /// <param name="inner">Inner exception.</param>
        public S3Exception(Error? error = null, Exception? inner = null)
            : base(error?.Message, inner)
        {
            Error = error;
            InnerException = inner;
        }

        #endregion

        #region Public-Methods

        #endregion

        #region Private-Methods

        #endregion
    }
