#nullable disable
// Port of the SRP calculation in the APK's CognitoUser.AuthenticationHelper.
using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ArrisControl {
    public sealed class CognitoSrp {
        private const string PrimeHex = "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";
        private static readonly BigInteger N = Hex(PrimeHex);
        private static readonly BigInteger G = new BigInteger(2);
        private static readonly BigInteger K = Positive(Hash(Join(JavaBytes(N), JavaBytes(G))));
        private readonly BigInteger a;
        private readonly BigInteger publicA;
        private readonly string poolName;
        public CognitoSrp(string poolId) : this(poolId, null) {}
        // Non-random constructor is for offline test vectors only.
        public CognitoSrp(string poolId, string testPrivateHex) {
            poolName = poolId.Contains("_") ? poolId.Split(new char[]{'_'}, 2)[1] : poolId;
            if (testPrivateHex != null) a = Hex(testPrivateHex);
            else {
                byte[] random = new byte[128];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
                a = Positive(random) % N;
                Array.Clear(random, 0, random.Length);
            }
            if (a <= BigInteger.Zero) throw new ArgumentException("SRP private value must be positive.");
            publicA = BigInteger.ModPow(G, a, N);
            if (publicA % N == BigInteger.Zero) throw new ArgumentException("Invalid SRP public value.");
        }
        public string PublicA { get { return publicA.ToString("x", CultureInfo.InvariantCulture).TrimStart('0'); } }
        private static BigInteger Hex(string hex) {
            if (String.IsNullOrEmpty(hex)) throw new ArgumentException("Missing SRP parameter.");
            foreach (char c in hex) if (!Uri.IsHexDigit(c)) throw new ArgumentException("Invalid SRP hexadecimal parameter.");
            return BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        private static BigInteger Positive(byte[] bytes) {
            byte[] little = new byte[bytes.Length + 1];
            for (int i=0; i<bytes.Length; i++) little[i] = bytes[bytes.Length-1-i];
            return new BigInteger(little);
        }
        private static byte[] JavaBytes(BigInteger value) {
            if (value < 0) throw new ArgumentException("Expected nonnegative integer.");
            byte[] bytes = value.ToByteArray();
            Array.Reverse(bytes);
            return bytes;
        }
        private static byte[] Join(params byte[][] arrays) {
            int length=0;
            foreach (byte[] arr in arrays) length += arr.Length;
            byte[] output = new byte[length];
            int offset=0;
            foreach (byte[] arr in arrays) { Buffer.BlockCopy(arr,0,output,offset,arr.Length); offset += arr.Length; }
            return output;
        }
        private static byte[] Hash(byte[] data) { using (SHA256 hash=SHA256.Create()) return hash.ComputeHash(data); }
        private static byte[] Hmac(byte[] key, byte[] data) { using (HMACSHA256 hmac=new HMACSHA256(key)) return hmac.ComputeHash(data); }
        public byte[] ComputeKey(string userId, string password, string serverBHex, string saltHex) {
            BigInteger serverB=Hex(serverBHex), salt=Hex(saltHex);
            if (serverB % N == 0) throw new ArgumentException("SRP server B cannot be zero.");
            BigInteger u=Positive(Hash(Join(JavaBytes(publicA),JavaBytes(serverB))));
            if (u == 0) throw new ArgumentException("SRP u cannot be zero.");
            byte[] identityHash=Hash(Encoding.UTF8.GetBytes(poolName+userId+":"+password));
            BigInteger x=Positive(Hash(Join(JavaBytes(salt),identityHash)));
            BigInteger basis=(serverB-K*BigInteger.ModPow(G,x,N)) % N;
            if (basis < 0) basis += N;
            BigInteger shared=BigInteger.ModPow(basis,a+u*x,N);
            byte[] prk=Hmac(JavaBytes(u),JavaBytes(shared));
            byte[] expanded=Hmac(prk,Join(Encoding.UTF8.GetBytes("Caldera Derived Key"),new byte[]{1}));
            byte[] key=new byte[16];
            Buffer.BlockCopy(expanded,0,key,0,16);
            Array.Clear(prk,0,prk.Length);
            Array.Clear(expanded,0,expanded.Length);
            return key;
        }
        public string Sign(string userId,string password,string serverBHex,string saltHex,string secretBlock,string timestamp) {
            byte[] key=ComputeKey(userId,password,serverBHex,saltHex);
            try {
                return Convert.ToBase64String(Hmac(key,Join(Encoding.UTF8.GetBytes(poolName),Encoding.UTF8.GetBytes(userId),Convert.FromBase64String(secretBlock),Encoding.UTF8.GetBytes(timestamp))));
            } finally { Array.Clear(key,0,key.Length); }
        }
    }
}
