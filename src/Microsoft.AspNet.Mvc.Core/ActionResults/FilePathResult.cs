// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.FileProviders;
using Microsoft.AspNet.Hosting;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Mvc.Core;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Internal;

namespace Microsoft.AspNet.Mvc
{
    /// <summary>
    /// An <see cref="ActionResult"/> that when executed will
    /// write a file from disk to the response using mechanisms provided
    /// by the host.
    /// </summary>
    public class FilePathResult : FileResult
    {
        private const int DefaultBufferSize = 0x1000;

        private string _fileName;

        /// <summary>
        /// Creates a new <see cref="FilePathResult"/> instance with
        /// the provided <paramref name="fileName"/>
        /// </summary>
        /// <param name="fileName">The path to the file. The path must be an absolute
        /// path. Relative and virtual paths are not supported.</param>
        /// <param name="contentType">The Content-Type header of the response.</param>
        public FilePathResult([NotNull] string fileName)
            : base(contentType: null)
        {
            FileName = fileName;
        }

        /// <summary>
        /// Creates a new <see cref="FilePathResult"/> instance with
        /// the provided <paramref name="fileName"/> and the
        /// provided <paramref name="contentType"/>.
        /// </summary>
        /// <param name="fileName">The path to the file. The path must be an absolute
        /// path. Relative and virtual paths are not supported.</param>
        /// <param name="contentType">The Content-Type header of the response.</param>
        public FilePathResult([NotNull] string fileName, string contentType)
            : base(contentType)
        {
            FileName = fileName;
        }

        /// <summary>
        /// Gets or sets the path to the file that will be sent back as the response.
        /// </summary>
        public string FileName
        {
            get
            {
                return _fileName;
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value");
                }

                _fileName = value;
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="IFileProvider"/> used to resolve paths.
        /// </summary>
        public IFileProvider FileProvider { get; set; }

        /// <inheritdoc />
        protected override Task WriteFileAsync(HttpResponse response, CancellationToken cancellation)
        {
            var filePath = NormalizePath(FileName);

            if (IsPathRooted(filePath))
            {
                return CopyPhysicalFileToResponseAsync(response, filePath, cancellation);
            }
            else
            {
                var fileProvider = GetFileProvider(response.HttpContext.RequestServices);
                var fileInfo = fileProvider.GetFileInfo(subpath: filePath);
                if (fileInfo.Exists)
                {
                    if (fileInfo.PhysicalPath != null)
                    {
                        return CopyPhysicalFileToResponseAsync(response, fileInfo.PhysicalPath, cancellation);
                    }
                    else
                    {
                        // Example: An embedded resource
                        return CopyStreamToResponseAsync(fileInfo.CreateReadStream(), response, cancellation);
                    }
                }
                else
                {
                    var message = Resources.FormatFileResult_InvalidPath(fileInfo.PhysicalPath);
                    throw new FileNotFoundException(message, fileInfo.PhysicalPath);
                }
            }
        }
        
        /// <summary>
        /// Creates a normalized representation of the given <paramref name="path"/>. The default
        /// implementation doesn't support files with '\' in the file name and treats the '\' as
        /// a directory separator. The default implementation will convert all the '\' into '/'
        /// and will remove leading '~' characters.
        /// </summary>
        /// <param name="path">The path to normalize.</param>
        /// <returns>The normalized path.</returns>
        // Internal for unit testing purposes only
        protected internal virtual string NormalizePath([NotNull] string path)
        {
            // Unix systems support '\' as part of the file name. So '\' is not
            // a valid directory separator in those systems. Here we make the conscious
            // choice of replacing '\' for '/' which means that file names with '\' will
            // not be supported.

            if (path.StartsWith("~/", StringComparison.Ordinal))
            {
                // We don't support virtual paths for now, so we just treat them as relative
                // paths.
                return path.Substring(1).Replace('\\', '/');
            }

            if (path.StartsWith("~\\", StringComparison.Ordinal))
            {
                // ~\ is not a valid virtual path, and we don't want to replace '\' with '/' as it
                // obfuscates the error, so just return the original path and throw at a later point
                // when we can't find the file.
                return path;
            }

            return path.Replace('\\', '/');
        }

        /// <summary>
        /// Determines if the provided path is absolute or relative. The default implementation considers
        /// paths starting with '/' to be relative.
        /// </summary>
        /// <param name="path">The path to examine.</param>
        /// <returns>True if the path is absolute.</returns>
        // Internal for unit testing purposes only
        protected internal virtual bool IsPathRooted([NotNull] string path)
        {
            // We consider paths to be rooted if they start with '<<VolumeLetter>>:' and do
            // not start with '\' or '/'. In those cases, even that the paths are 'traditionally'
            // rooted, we consider them to be relative.
            // In Unix rooted paths start with '/' which is not supported by this action result
            // by default.

            return Path.IsPathRooted(path) && (IsNetworkPath(path) || !StartsWithForwardOrBackSlash(path));
        }

        private static bool StartsWithForwardOrBackSlash(string path)
        {
            return path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith("\\", StringComparison.Ordinal);
        }

        private static bool IsNetworkPath(string path)
        {
            return path.StartsWith("//", StringComparison.Ordinal) ||
                path.StartsWith("\\\\", StringComparison.Ordinal);
        }

        private IFileProvider GetFileProvider(IServiceProvider requestServices)
        {
            if (FileProvider != null)
            {
                return FileProvider;
            }

            var hostingEnvironment = requestServices.GetService<IHostingEnvironment>();
            FileProvider = hostingEnvironment.WebRootFileProvider;

            return FileProvider;
        }

        private Task CopyPhysicalFileToResponseAsync(
            HttpResponse response,
            string physicalFilePath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(physicalFilePath))
            {
                var message = Resources.FormatFileResult_InvalidPath(physicalFilePath);
                throw new FileNotFoundException(message, physicalFilePath);
            }

            var sendFile = response.HttpContext.GetFeature<IHttpSendFileFeature>();
            if (sendFile != null)
            {
                return sendFile.SendFileAsync(
                    physicalFilePath,
                    offset: 0,
                    length: null,
                    cancellation: cancellationToken);
            }
            else
            {
                return CopyStreamToResponseAsync(physicalFilePath, response, cancellationToken);
            }
        }

        private static Task CopyStreamToResponseAsync(
            string fileName,
            HttpResponse response,
            CancellationToken cancellation)
        {
            var fileStream = new FileStream(
                fileName, FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return CopyStreamToResponseAsync(fileStream, response, cancellation);
        }

        private static async Task CopyStreamToResponseAsync(
            Stream sourceStream,
            HttpResponse response,
            CancellationToken cancellation)
        {
            using (sourceStream)
            {
                await sourceStream.CopyToAsync(response.Body, DefaultBufferSize, cancellation);
            }
        }
    }
}