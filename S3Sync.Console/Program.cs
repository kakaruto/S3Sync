using System;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

        }

        private static void GetFilesOnS3()
        {
            ListObjectsResponse listObjectsResponse = _amazonS3Client.ListObjects(new ListObjectsRequest { BucketName = BucketName });
            foreach (S3Object s3Object in listObjectsResponse.S3Objects)
            {
                FileInfo fileInfo = new FileInfo(Path.Combine(_folder, s3Object.Key));
                if (fileInfo.Exists)
                {
                    System.Console.WriteLine(s3Object.Key + " exist");

                    int dateCompare = DateTime.Compare(fileInfo.LastWriteTime, DateTime.Parse(s3Object.LastModified));

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

            new FileInfo(filePath).LastWriteTime = DateTime.Parse(s3Object.LastModified);
        }

        private static void Main()
        {
            Initialize();
            GetFilesOnS3();

            System.Console.WriteLine("Waiting... (q to exit)");
            while (System.Console.Read() != 'q')
            {
            }

            _amazonS3Client.Dispose();
        }
    }
}
