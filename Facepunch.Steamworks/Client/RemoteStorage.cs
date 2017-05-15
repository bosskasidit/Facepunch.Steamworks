﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SteamNative;

namespace Facepunch.Steamworks
{
    partial class Client
    {
        RemoteStorage _remoteStorage;

        public RemoteStorage RemoteStorage
        {
            get
            {
                if ( _remoteStorage == null )
                    _remoteStorage = new RemoteStorage( this );

                return _remoteStorage;
            }
        }
    }

    internal class RemoteFileWriteStream : Stream
    {
        internal readonly RemoteStorage remoteStorage;

        private readonly UGCFileWriteStreamHandle_t _handle;
        private readonly RemoteFile _file;

        private int _written;
        private bool _closed;

        internal RemoteFileWriteStream( RemoteStorage r, RemoteFile file )
        {
            remoteStorage = r;

            _handle = remoteStorage.native.FileWriteStreamOpen( file.FileName );
            _file = file;
        }

        public override void Flush() { }

        public override int Read( byte[] buffer, int offset, int count )
        {
            throw new NotImplementedException();
        }

        public override long Seek( long offset, SeekOrigin origin )
        {
            throw new NotImplementedException();
        }

        public override void SetLength( long value )
        {
            throw new NotImplementedException();
        }

        public override unsafe void Write( byte[] buffer, int offset, int count )
        {
            if ( _closed ) throw new ObjectDisposedException( ToString() );

            fixed ( byte* bufferPtr = buffer )
            {
                if ( remoteStorage.native.FileWriteStreamWriteChunk( _handle, (IntPtr) (bufferPtr + offset), count ) )
                {
                    _written += count;
                }
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get { return _written; } set { throw new NotImplementedException(); } }

        public void Cancel()
        {
            if ( _closed ) return;

            _closed = true;
            remoteStorage.native.FileWriteStreamCancel( _handle );
        }

        public void Close()
        {
            if ( _closed ) return;

            _closed = true;
            remoteStorage.native.FileWriteStreamClose( _handle );

            _file.remoteStorage.OnWrittenNewFile( _file );
        }

        protected override void Dispose( bool disposing )
        {
            if ( disposing ) Close();
            base.Dispose( disposing );
        }
    }

    public class RemoteFile
    {
        internal readonly RemoteStorage remoteStorage;

        private readonly bool _isUgc;
        private string _fileName;
        private int _sizeInBytes = -1;
        private UGCHandle_t _handle;
        private ulong _ownerId;

        public bool Exists { get; internal set; }

        public string FileName
        {
            get
            {
                if ( _fileName != null ) return _fileName;
                GetUGCDetails();
                return _fileName;
            }
        }

        public ulong OwnerId
        {
            get
            {
                if ( _ownerId != 0 ) return _ownerId;
                GetUGCDetails();
                return _ownerId;
            }
        }

        public int SizeInBytes
        {
            get
            {
                if ( _sizeInBytes != -1 ) return _sizeInBytes;
                if ( _isUgc ) throw new NotImplementedException();
                _sizeInBytes = remoteStorage.native.GetFileSize( FileName );
                return _sizeInBytes;
            }
            internal set { _sizeInBytes = value; }
        }

        internal RemoteFile( RemoteStorage r, UGCHandle_t handle )
        {
            Exists = true;

            remoteStorage = r;

            _isUgc = true;
            _handle = handle;
        }

        internal RemoteFile( RemoteStorage r, string name, ulong ownerId, int sizeInBytes = -1 )
        {
            remoteStorage = r;

            _isUgc = false;
            _fileName = name;
            _ownerId = ownerId;
            _sizeInBytes = sizeInBytes;
        }

        public Stream OpenWrite()
        {
            return new RemoteFileWriteStream( remoteStorage, this );
        }

        public void WriteAllBytes( byte[] buffer )
        {
            using ( var stream = OpenWrite() )
            {
                stream.Write( buffer, 0, buffer.Length );
            }
        }

        public void WriteAllText( string text, Encoding encoding = null )
        {
            if ( encoding == null ) encoding = Encoding.UTF8;
            WriteAllBytes( encoding.GetBytes( text ) );
        }

        public Stream OpenRead()
        {
            return new MemoryStream( ReadAllBytes(), false );
        }

        public unsafe byte[] ReadAllBytes()
        {
            if ( _isUgc )
            {
                // Need to download
                throw new NotImplementedException();
            }

            var size = SizeInBytes;
            var buffer = new byte[size];

            fixed ( byte* bufferPtr = buffer )
            {
                remoteStorage.native.FileRead( FileName, (IntPtr) bufferPtr, size );
            }

            return buffer;
        }

        public string ReadAllText( Encoding encoding = null )
        {
            if ( encoding == null ) encoding = Encoding.UTF8;
            return encoding.GetString( ReadAllBytes() );
        }

        public delegate void ShareCallback( bool success );
        public bool Share( ShareCallback callback = null )
        {
            if ( _isUgc ) return false;

            // Already shared
            if ( _handle.Value != 0 ) return false;

            remoteStorage.native.FileShare( FileName, ( result, error ) =>
            {
                var success = !error && result.Result == Result.OK;
                if ( success )
                {
                    _handle.Value = result.File;
                }

                callback?.Invoke( success );
            } );

            return true;
        }

        public bool Delete()
        {
            if ( !Exists ) return false;
            if ( _isUgc ) return false;
            if ( !remoteStorage.native.FileDelete( FileName ) ) return false;

            Exists = false;
            remoteStorage.InvalidateFiles();

            return true;
        }

        private void GetUGCDetails()
        {
            if ( !_isUgc ) throw new InvalidOperationException();

            var appId = new AppId_t { Value = remoteStorage.native.steamworks.AppId };

            CSteamID ownerId;
            remoteStorage.native.GetUGCDetails( _handle, ref appId, out _fileName, out ownerId );

            _ownerId = ownerId.Value;
        }
    }

    /// <summary>
    /// Handles Steam Cloud related actions.
    /// </summary>
    public class RemoteStorage
    {
        private static string NormalizePath( string path )
        {
            return new FileInfo( $"x:/{path}" ).FullName.Substring( 3 );
        }

        internal readonly Client client;
        internal readonly SteamNative.SteamRemoteStorage native;

        private bool _filesInvalid = true;
        private readonly List<RemoteFile> _files = new List<RemoteFile>();

        internal RemoteStorage( Client c )
        {
            client = c;
            native = client.native.remoteStorage;
        }

        /// <summary>
        /// True if Steam Cloud is currently enabled by the current user.
        /// </summary>
        public bool IsCloudEnabledForAccount
        {
            get { return native.IsCloudEnabledForAccount(); }
        }

        /// <summary>
        /// True if Steam Cloud is currently enabled for this app by the current user.
        /// </summary>
        public bool IsCloudEnabledForApp
        {
            get { return native.IsCloudEnabledForApp(); }
        }

        public int FileCount
        {
            get { return native.GetFileCount(); }
        }

        public IEnumerable<RemoteFile> Files
        {
            get
            {
                UpdateFiles();
                return _files;
            }
        }

        public RemoteFile CreateFile( string path )
        {
            path = NormalizePath( path );

            InvalidateFiles();
            var existing = Files.FirstOrDefault( x => x.FileName == path );
            return existing ?? new RemoteFile( this, path, client.SteamId, 0 );
        }

        internal void OnWrittenNewFile( RemoteFile file )
        {
            if ( _files.Any( x => x.FileName == file.FileName ) ) return;

            _files.Add( file );
            file.Exists = true;

            InvalidateFiles();
        }

        internal void InvalidateFiles()
        {
            _filesInvalid = true;
        }

        private void UpdateFiles()
        {
            if ( !_filesInvalid ) return;
            _filesInvalid = false;

            foreach ( var file in _files )
            {
                file.Exists = false;
            }

            var count = FileCount;
            for ( var i = 0; i < count; ++i )
            {
                int size;
                var name = NormalizePath( GetFileNameAndSize( i, out size ) );

                var existing = _files.FirstOrDefault( x => x.FileName == name );
                if ( existing == null )
                {
                    existing = new RemoteFile( this, name, client.SteamId, size );
                    _files.Add( existing );
                }
                else
                {
                    existing.SizeInBytes = size;
                }

                existing.Exists = true;
            }

            for ( var i = _files.Count - 1; i >= 0; --i )
            {
                if ( !_files[i].Exists ) _files.RemoveAt( i );
            }
        }

        public bool FileExists( string path )
        {
            return native.FileExists( path );
        }

        /// <summary>
        /// Gets both the total and available remote storage in bytes for this user and app.
        /// </summary>
        /// <returns>True if successful</returns>
        public unsafe bool GetQuota( out ulong totalBytes, out ulong availableBytes )
        {
            fixed ( ulong* totalPtr = &totalBytes)
            fixed ( ulong* availablePtr = &availableBytes )
            {
                return native.GetQuota( (IntPtr) totalPtr, (IntPtr) availablePtr );
            }
        }

        private unsafe string GetFileNameAndSize( int file, out int size )
        {
            fixed ( int* sizePtr = &size )
            {
                return native.GetFileNameAndSize( file, (IntPtr) sizePtr );
            }
        }
    }
}
