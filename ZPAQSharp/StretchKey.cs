using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ZPAQSharp
{
	class StretchKey
	{
		// Strengthen password pw[0..pwlen-1] and salt[0..saltlen-1]
		// to produce key buf[0..buflen-1]. Uses O(n*r*p) time and 128*r*n bytes
		// of memory. n must be a power of 2 and r <= 8.
		void scrypt(string pw, int pwlen, string salt, int saltlen, int n, int r, int p, char[] buf, int buflen)
		{
		}

		// Generate a strong key out[0..31] key[0..31] and salt[0..31].
		// Calls scrypt(key, 32, salt, 32, 16384, 8, 1, out, 32);
		void stretchKey(char[] @out, string key, string salt)
		{
		}

		// PBKDF2(pw[0..pwlen], salt[0..saltlen], c) to buf[0..dkLen-1]
		// using HMAC-SHA256, for the special case of c = 1 iterations
		// output size dkLen a multiple of 32, and pwLen <= 64.
		static void pbkdf2(string pw, int pwLen, string salt, int saltLen, int c, char[] buf, int dkLen)
		{
			Debug.Assert(c==1);
			Debug.Assert(dkLen%32==0);
			Debug.Assert(pwLen<=64);

			SHA256 sha256 = SHA256.Create();
			char[] b = new char[32];
			for (int i = 1; i * 32 <= dkLen; ++i)
			{
				for (int j = 0; j < pwLen; ++j)
				{
					sha256.put(pw[j] ^ 0x36);
				}

				for (int j = pwLen; j < 64; ++j)
				{
					sha256.put(0x36);
				}

				for (int j = 0; j < saltLen; ++j)
				{
					sha256.put(salt[j]);
				}

				for (int j = 24; j >= 0; j -= 8)
				{
					sha256.put(i >> j);
				}

				b = sha256.Hash.Select(by => (char)by).ToArray();

				for (int j = 0; j < pwLen; ++j)
				{
					sha256.put(pw[j] ^ 0x5c);
				}

				for (int j = pwLen; j < 64; ++j)
				{
					sha256.put(0x5c);
				}

				for (int j = 0; j < 32; ++j)
				{
					sha256.put(b[j]);
				}

				memcpy(buf+i*32-32, sha256.result(), 32);
			}
		}

		// Hash b[0..15] using 8 rounds of salsa20
		// Modified from http://cr.yp.to/salsa20.html (public domain) to 8 rounds
		static void salsa8(U32* b)
		{
			unsigned x[16] = { 0 };
			memcpy(x, b, 64);
			for (int i = 0; i < 4; ++i)
			{
#define R(a,b) (((a)<<(b))+((a)>>(32-b)))
				x[4] ^= R(x[0] + x[12], 7); x[8] ^= R(x[4] + x[0], 9);
				x[12] ^= R(x[8] + x[4], 13); x[0] ^= R(x[12] + x[8], 18);
				x[9] ^= R(x[5] + x[1], 7); x[13] ^= R(x[9] + x[5], 9);
				x[1] ^= R(x[13] + x[9], 13); x[5] ^= R(x[1] + x[13], 18);
				x[14] ^= R(x[10] + x[6], 7); x[2] ^= R(x[14] + x[10], 9);
				x[6] ^= R(x[2] + x[14], 13); x[10] ^= R(x[6] + x[2], 18);
				x[3] ^= R(x[15] + x[11], 7); x[7] ^= R(x[3] + x[15], 9);
				x[11] ^= R(x[7] + x[3], 13); x[15] ^= R(x[11] + x[7], 18);
				x[1] ^= R(x[0] + x[3], 7); x[2] ^= R(x[1] + x[0], 9);
				x[3] ^= R(x[2] + x[1], 13); x[0] ^= R(x[3] + x[2], 18);
				x[6] ^= R(x[5] + x[4], 7); x[7] ^= R(x[6] + x[5], 9);
				x[4] ^= R(x[7] + x[6], 13); x[5] ^= R(x[4] + x[7], 18);
				x[11] ^= R(x[10] + x[9], 7); x[8] ^= R(x[11] + x[10], 9);
				x[9] ^= R(x[8] + x[11], 13); x[10] ^= R(x[9] + x[8], 18);
				x[12] ^= R(x[15] + x[14], 7); x[13] ^= R(x[12] + x[15], 9);
				x[14] ^= R(x[13] + x[12], 13); x[15] ^= R(x[14] + x[13], 18);
#undef R
			}
			for (int i = 0; i < 16; ++i) b[i] += x[i];
		}

		// BlockMix_{Salsa20/8, r} on b[0..128*r-1]
		static void blockmix(U32* b, int r)
		{
			assert(r <= 8);
			U32 x[16];
			U32 y[256];
			memcpy(x, b + 32 * r - 16, 64);
			for (int i = 0; i < 2 * r; ++i)
			{
				for (int j = 0; j < 16; ++j) x[j] ^= b[i * 16 + j];
				salsa8(x);
				memcpy(&y[i * 16], x, 64);
			}
			for (int i = 0; i < r; ++i) memcpy(b + i * 16, &y[i * 32], 64);
			for (int i = 0; i < r; ++i) memcpy(b + (i + r) * 16, &y[i * 32 + 16], 64);
		}

		// Mix b[0..128*r-1]. Uses 128*r*n bytes of memory and O(r*n) time
		static void smix(char* b, int r, int n)
		{
			libzpaq::Array<U32> x(32 * r), v(32 * r * n);
			for (int i = 0; i < r * 128; ++i) x[i / 4] += (b[i] & 255) << i % 4 * 8;
			for (int i = 0; i < n; ++i)
			{
				memcpy(&v[i * r * 32], &x[0], r * 128);
				blockmix(&x[0], r);
			}
			for (int i = 0; i < n; ++i)
			{
				U32 j = x[(2 * r - 1) * 16] & (n - 1);
				for (int k = 0; k < r * 32; ++k) x[k] ^= v[j * r * 32 + k];
				blockmix(&x[0], r);
			}
			for (int i = 0; i < r * 128; ++i) b[i] = x[i / 4] >> (i % 4 * 8);
		}

		// Strengthen password pw[0..pwlen-1] and salt[0..saltlen-1]
		// to produce key buf[0..buflen-1]. Uses O(n*r*p) time and 128*r*n bytes
		// of memory. n must be a power of 2 and r <= 8.
		void scrypt(const char* pw, int pwlen,

			const char* salt, int saltlen,

			int n, int r, int p, char* buf, int buflen) {
  assert(r<=8);
  assert(n>0 && (n&(n-1))==0);  // power of 2?
  libzpaq::Array<char> b(p* r*128);
  pbkdf2(pw, pwlen, salt, saltlen, 1, &b[0], p* r*128);
  for (int i=0; i<p; ++i) smix(&b[i * r * 128], r, n);
  pbkdf2(pw, pwlen, &b[0], p* r*128, 1, buf, buflen);
	}

	// Stretch key in[0..31], assumed to be SHA256(password), with
	// NUL terminate salt to produce new key out[0..31]
	void stretchKey(char* out, const char* in, const char* salt)
	{
		scrypt(in, 32, salt, 32, 1 << 14, 8, 1, out, 32);
	}
}
}
