using System;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3Sync.Console
{
    public class Program
    {
        // Le nom est unique pour tous les utilisateurs !
        private const string BucketName = "DropBoxClone42";

        private static string _folder;

        private static AmazonS3Client _amazonS3Client;

        private static void InitializeS3ClientAndFolder()
        {
            NameValueCollection appConfig = ConfigurationManager.AppSettings;
            _amazonS3Client = new AmazonS3Client(appConfig["AWSAccessKey"], appConfig["AWSSecretKey"]);

            _folder = appConfig["SyncFolder"];

            ListBucketsResponse listBucketsResponse = _amazonS3Client.ListBuckets();
            bool bucketIsExist = listBucketsResponse.Buckets.Any(s3Bucket => s3Bucket.BucketName == BucketName);

            if ( !bucketIsExist )
            {
                _amazonS3Client.PutBucket(new PutBucketRequest().WithBucketName(BucketName));
            }
        }

        private static void GetFilesOnS3()
        {
            ListObjectsResponse listObjectsResponse = _amazonS3Client.ListObjects(new ListObjectsRequest {BucketName = BucketName});
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
                    }
                }
                else
                {
                    System.Console.WriteLine(s3Object.Key + " doesn't exist");
                    DownloadFile(s3Object);
                }
            }
        }

        private static void DownloadFile(S3Object s3Object)
        {
            System.Console.WriteLine("Downloading " + s3Object.Key);
            GetObjectResponse getObjectResponse = _amazonS3Client.GetObject(new GetObjectRequest { BucketName = BucketName, Key = s3Object .Key});

            string filePath = Path.Combine(_folder, s3Object.Key);
            using(FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using(BufferedStream bufferedStream = new BufferedStream(getObjectResponse.ResponseStream))
            {
                byte[] buffer = new byte[0x2000];
                int count;
                while ((count = bufferedStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fileStream.Write(buffer, 0, count);
                }
            }

            new FileInfo(filePath).LastWriteTime = DateTime.Parse(s3Object.LastModified);
        }

        private static void Main()
        {
            InitializeS3ClientAndFolder();
            GetFilesOnS3();

            while (System.Console.Read() != 'q')
            {
            }

            _amazonS3Client.Dispose();
        }
    }
}