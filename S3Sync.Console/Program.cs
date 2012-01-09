using System;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3Sync.Console
{
    public class Program
    {
        // Le nom est unique pour tous les utilisateurs !
        private const string BucketName = "DropBoxClone42";

        private static string _folder;

        private static ICryptoTransform _aesEncryptor;
        private static ICryptoTransform _aesDecryptor;

        private static AmazonS3Client _amazonS3Client;

        private static void Initialize()
        {
            NameValueCollection appConfig = ConfigurationManager.AppSettings;
            _amazonS3Client = new AmazonS3Client(appConfig["AWSAccessKey"], appConfig["AWSSecretKey"]);

            _folder = appConfig["SyncFolder"];

            string password = appConfig["CryptoKey"];
            Rfc2898DeriveBytes rfcDb = new Rfc2898DeriveBytes(password, System.Text.Encoding.UTF8.GetBytes(password));

            Rijndael rijndael = Rijndael.Create();

            byte[] key = rfcDb.GetBytes(32); //256 bits key
            byte[] iv = rfcDb.GetBytes(16); // 128 bits key
            _aesEncryptor = rijndael.CreateEncryptor(key, iv);
            _aesDecryptor = rijndael.CreateDecryptor(key, iv);

            ListBucketsResponse listBucketsResponse = _amazonS3Client.ListBuckets();
            bool bucketIsExist = listBucketsResponse.Buckets.Any(s3Bucket => s3Bucket.BucketName == BucketName);

            if (!bucketIsExist)
            {
                _amazonS3Client.PutBucket(new PutBucketRequest().WithBucketName(BucketName));
            }

            InitializeFileSystemWatcher();
        }

        private static void InitializeFileSystemWatcher()
        {
            FileSystemWatcher fileSystemWatcher = new FileSystemWatcher(_folder)
                                                      {
                                                          IncludeSubdirectories = false,
                                                          NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size
                                                      };

            fileSystemWatcher.Created += fileSystemWatcher_Changed;
            fileSystemWatcher.Changed += fileSystemWatcher_Changed;
            fileSystemWatcher.Deleted += fileSystemWatcher_Changed;
            fileSystemWatcher.Renamed += fileSystemWatcher_Renamed;

            // Begin watch
            fileSystemWatcher.EnableRaisingEvents = true;
        }

        private static void fileSystemWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            System.Console.WriteLine("Rename of " + e.OldName + " in " + e.Name);
            RenameS3Object(e.OldName, e.Name);
        }

        private static void fileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Created:
                    System.Console.WriteLine("Creation of " + e.Name);
                    UploadFile(new FileInfo(e.FullPath));
                    break;
                case WatcherChangeTypes.Changed:
                    System.Console.WriteLine("Change of " + e.Name);
                    UploadFile(new FileInfo(e.FullPath));
                    break;
                case WatcherChangeTypes.Deleted:
                    System.Console.WriteLine("Deletion of " + e.Name);
                    DeleteS3Object(e.Name);
                    break;

                default:
                    System.Console.WriteLine("Bug " + e.Name);
                    break;
            }
        }

        private static void GetFilesOnS3(object state)
        {
            System.Console.WriteLine("Check Files on S3...");
            ListObjectsResponse listObjectsResponse = _amazonS3Client.ListObjects(new ListObjectsRequest { BucketName = BucketName });
            
            foreach (S3Object s3Object in listObjectsResponse.S3Objects)
            {
                FileInfo fileInfo = new FileInfo(Path.Combine(_folder, s3Object.Key));
                if (fileInfo.Exists)
                {
                    System.Console.WriteLine(s3Object.Key + " exist");

                    GetObjectMetadataRequest getObjectMetadataRequest = new GetObjectMetadataRequest();
                    getObjectMetadataRequest.WithBucketName(BucketName).WithKey(s3Object.Key);
                    GetObjectMetadataResponse getObjectMetadataResponse = _amazonS3Client.GetObjectMetadata(getObjectMetadataRequest);

                    //int dateCompare = DateTime.Compare(fileInfo.LastWriteTime., DateTime.Parse(getObjectMetadataResponse.Metadata["x-amz-meta-LWT"]));
                    int dateCompare = (int)(fileInfo.LastWriteTime - DateTime.Parse(getObjectMetadataResponse.Metadata["x-amz-meta-LWT"])).TotalSeconds;

                    if (dateCompare == 0)
                    {
                        System.Console.WriteLine("No difference, do nothing");
                    }
                    else if (dateCompare < 0)
                    {
                        System.Console.WriteLine("S3 is newer");
                        DownloadFile(s3Object);
                    }
                    else if (dateCompare > 0)
                    {
                        System.Console.WriteLine("FS is newer");
                        UploadFile(fileInfo);
                    }
                }
                else
                {
                    System.Console.WriteLine(s3Object.Key + " doesn't exist");
                    DownloadFile(s3Object);
                }
            }


            foreach (string file in Directory.GetFiles(_folder))
            {
                FileInfo fileInfo = new FileInfo(file);

                if (!listObjectsResponse.S3Objects.Any(s => s.Key == fileInfo.Name))
                {
                    UploadFile(fileInfo);
                }
            }

        }

        private static void DeleteS3Object(string key)
        {
            DeleteObjectRequest deleteRequest = new DeleteObjectRequest
                                                    {
                                                        BucketName = BucketName,
                                                        Key = key
                                                    };
            _amazonS3Client.DeleteObject(deleteRequest);
        }

        private static void RenameS3Object(string oldKey, string newKey)
        {
            // Copy
            CopyObjectRequest request = new CopyObjectRequest
            {
                SourceBucket = BucketName,
                SourceKey = oldKey,
                DestinationBucket = BucketName,
                DestinationKey = newKey
            };
            _amazonS3Client.CopyObject(request);

            // Delete
            DeleteS3Object(oldKey);
        }

        private static void UploadFile(FileInfo fileInfo)
        {
            System.Console.WriteLine("Uploading " + fileInfo.Name);
            using (FileStream inputFileStream = new FileStream(fileInfo.FullName, FileMode.Open))
            using (MemoryStream outputMemoryStream = new MemoryStream())
            using (CryptoStream cryptoStream = new CryptoStream(outputMemoryStream, _aesEncryptor, CryptoStreamMode.Write))
            {
                int data;
                while ((data = inputFileStream.ReadByte()) != -1)
                {
                    cryptoStream.WriteByte((byte)data);
                }
                cryptoStream.FlushFinalBlock();

                PutObjectRequest createRequest = new PutObjectRequest();
                createRequest.WithMetaData("x-amz-meta-LWT", fileInfo.LastWriteTime.ToString("G"));
                createRequest.WithBucketName(BucketName);
                createRequest.WithKey(fileInfo.Name);
                createRequest.WithInputStream(outputMemoryStream);

                _amazonS3Client.PutObject(createRequest);
            }
        }

        private static void DownloadFile(S3Object s3Object)
        {
            System.Console.WriteLine("Downloading " + s3Object.Key);
            GetObjectResponse getObjectResponse = _amazonS3Client.GetObject(new GetObjectRequest { BucketName = BucketName, Key = s3Object.Key });
            
            string filePath = Path.Combine(_folder, s3Object.Key);

            using (BufferedStream inputBufferedStream = new BufferedStream(getObjectResponse.ResponseStream))
            using (CryptoStream cryptoStream = new CryptoStream(inputBufferedStream, _aesDecryptor, CryptoStreamMode.Read))
            using (FileStream outputFileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite))
            {
                int data;
                while ((data = cryptoStream.ReadByte()) != -1)
                {
                    outputFileStream.WriteByte((byte)data);
                }
            }

            new FileInfo(filePath).LastWriteTime = DateTime.Parse(getObjectResponse.Metadata["x-amz-meta-LWT"]);
        }

        private static void Main()
        {
            Initialize();

            Timer timer = new Timer(GetFilesOnS3, null, 0, 60 * 1000);

            System.Console.WriteLine("Waiting... (q to exit)");
            while (System.Console.Read() != 'q')
            {
            }

            _amazonS3Client.Dispose();
            timer.Dispose();
        }
    }
}
