using System.Collections.Specialized;
using System.Configuration;
using System.Linq;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3Sync.Console
{
    public class Program
    {
        // Le nom est unique pour tous les utilisateurs !
        private const string BucketName = "DropBoxClone42";

        private static AmazonS3Client _amazonS3Client;

        private static void InitializeS3Client()
        {
            NameValueCollection appConfig = ConfigurationManager.AppSettings;
            _amazonS3Client = new AmazonS3Client(appConfig["AWSAccessKey"], appConfig["AWSSecretKey"]);

            ListBucketsResponse listBucketsResponse = _amazonS3Client.ListBuckets();
            bool bucketIsExist = listBucketsResponse.Buckets.Any(s3Bucket => s3Bucket.BucketName == BucketName);

            if (!bucketIsExist)
            {
                _amazonS3Client.PutBucket(new PutBucketRequest().WithBucketName(BucketName));
            }
        }

        private static void Main(string[] args)
        {
            InitializeS3Client();

            while (System.Console.Read() != 'q')
            {
            }
        }
    }
}