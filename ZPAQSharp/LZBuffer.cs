using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZPAQSharp
{
	// Encode inbuf to buf using LZ77. args are as follows:
	// args[0] is log2 buffer size in MB.
	// args[1] is level (1=var. length, 2=byte aligned lz77, 3=bwt) + 4 if E8E9.
	// args[2] is the lz77 minimum match length and context order.
	// args[3] is the lz77 higher context order to search first, or else 0.
	// args[4] is the log2 hash bucket size (number of searches).
	// args[5] is the log2 hash table size. If 21+args[0] then use a suffix array.
	// args[6] is the secondary context look ahead
	// sap is pointer to external suffix array of inbuf or 0. If supplied and
	//   args[0]=5..7 then it is assumed that E8E9 was already applied to
	//   both the input and sap and the input buffer is not modified.
	class LZBuffer : Reader
	{
		libzpaq::Array<unsigned> ht;// hash table, confirm in low bits, or SA+ISA
		const unsigned char* in;    // input pointer
  const int checkbits;        // hash confirmation size or lg(ISA size)
		const int level;            // 1=var length LZ77, 2=byte aligned LZ77, 3=BWT
		const unsigned htsize;      // size of hash table
		const unsigned n;           // input length
		unsigned i;                 // current location in in (0 <= i < n)
		const unsigned minMatch;    // minimum match length
		const unsigned minMatch2;   // second context order or 0 if not used
		const unsigned maxMatch;    // longest match length allowed
		const unsigned maxLiteral;  // longest literal length allowed
		const unsigned lookahead;   // second context look ahead
		unsigned h1, h2;            // low, high order context hashes of in[i..]
		const unsigned bucket;      // number of matches to search per hash - 1
		const unsigned shift1, shift2;  // how far to shift h1, h2 per hash
		const int minMatchBoth;     // max(minMatch, minMatch2)
		const unsigned rb;          // number of level 1 r bits in match code
		unsigned bits;              // pending output bits (level 1)
		unsigned nbits;             // number of bits in bits
		unsigned rpos, wpos;        // read, write pointers
		unsigned idx;               // BWT index
		const unsigned* sa;         // suffix array for BWT or LZ77-SA
		unsigned* isa;              // inverse suffix array for LZ77-SA
		enum { BUFSIZE = 1 << 14 };       // output buffer size
		unsigned char buf[BUFSIZE]; // output buffer

		void write_literal(unsigned i, unsigned& lit);
		void write_match(unsigned len, unsigned off);
		void fill();  // encode to buf

		// write k bits of x
		void putb(unsigned x, int k)
		{
			x &= (1 << k) - 1;
			bits |= x << nbits;
			nbits += k;
			while (nbits > 7)
			{
				assert(wpos < BUFSIZE);
				buf[wpos++] = bits, bits >>= 8, nbits -= 8;
			}
		}

		// write last byte
		void flush()
		{
			assert(wpos < BUFSIZE);
			if (nbits > 0) buf[wpos++] = bits;
			bits = nbits = 0;
		}

		// write 1 byte
		void put(int c)
		{
			assert(wpos < BUFSIZE);
			buf[wpos++] = c;
		}

		public:
  LZBuffer(StringBuffer& inbuf, int args[], const unsigned* sap = 0);

		// return 1 byte of compressed output (overrides Reader)
		int get()
		{
			int c = -1;
			if (rpos == wpos) fill();
			if (rpos < wpos) c = buf[rpos++];
			if (rpos == wpos) rpos = wpos = 0;
			return c;
		}

		// Read up to p[0..n-1] and return bytes read.
		int read(char* p, int n);

		// LZ/BWT preprocessor for levels 1..3 compression and e8e9 filter.
		// Level 1 uses variable length LZ77 codes like in the lazy compressor:
		//
		//   00,n,L[n] = n literal bytes
		//   mm,mmm,n,ll,r,q (mm > 00) = match 4*n+ll at offset (q<<rb)+r-1
		//
		// where q is written in 8mm+mmm-8 (0..23) bits with an implied leading 1 bit
		// and n is written using interleaved Elias Gamma coding, i.e. the leading
		// 1 bit is implied, remaining bits are preceded by a 1 and terminated by
		// a 0. e.g. abc is written 1,b,1,c,0. Codes are packed LSB first and
		// padded with leading 0 bits in the last byte. r is a number with rb bits,
		// where rb = log2(blocksize) - 24.
		//
		// Level 2 is byte oriented LZ77 with minimum match length m = $4 = args[3]
		// with m in 1..64. Lengths and offsets are MSB first:
		// 00xxxxxx   x+1 (1..64) literals follow
		// yyxxxxxx   y+1 (2..4) offset bytes follow, match length x+m (m..m+63)
		//
		// Level 3 is BWT with the end of string byte coded as 255 and the
		// last 4 bytes giving its position LSB first.

		// floor(log2(x)) + 1 = number of bits excluding leading zeros (0..32)
		int lg(unsigned x)
		{
			unsigned r = 0;
			if (x >= 65536) r = 16, x >>= 16;
			if (x >= 256) r += 8, x >>= 8;
			if (x >= 16) r += 4, x >>= 4;
			assert(x >= 0 && x < 16);
			return
			  "\x00\x01\x02\x02\x03\x03\x03\x03\x04\x04\x04\x04\x04\x04\x04\x04"[x] + r;
		}

		// return number of 1 bits in x
		int nbits(unsigned x)
		{
			int r;
			for (r = 0; x; x >>= 1) r += x & 1;
			return r;
		}

		// Read n bytes of compressed output into p and return number of
		// bytes read in 0..n. 0 signals EOF (overrides Reader).
		int LZBuffer::read(char* p, int n)
		{
			if (rpos == wpos) fill();
			int nr = n;
			if (nr > int(wpos - rpos)) nr = wpos - rpos;
			if (nr) memcpy(p, buf + rpos, nr);
			rpos += nr;
			assert(rpos <= wpos);
			if (rpos == wpos) rpos = wpos = 0;
			return nr;
		}

		LZBuffer::LZBuffer(StringBuffer& inbuf, int args[], const unsigned* sap):

	ht((args[1]&3)==3 ? (inbuf.size()+1)*!sap      // for BWT suffix array
        : args[5]-args[0]<21 ? 1u<<args[5]         // for LZ77 hash table
        : (inbuf.size() *!sap)+(1u<<17<<args[0])),  // for LZ77 SA and ISA
    in(inbuf.data()),

	checkbits(args[5]-args[0]<21 ? 12-args[0] : 17+args[0]),

	level(args[1]&3),

	htsize(ht.size()),

	n(inbuf.size()),

	i(0),

	minMatch(args[2]),

	minMatch2(args[3]),

	maxMatch(BUFSIZE*3),

	maxLiteral(BUFSIZE/4),

	lookahead(args[6]),

	h1(0), h2(0),

	bucket((1<<args[4])-1), 

	shift1(minMatch>0 ? (args[5]-1)/minMatch+1 : 1),

	shift2(minMatch2>0 ? (args[5]-1)/minMatch2+1 : 0),

	minMatchBoth(MAX(minMatch, minMatch2+lookahead)+4),

	rb(args[0]>4 ? args[0]-4 : 0),

	bits(0), nbits(0), rpos(0), wpos(0),

	idx(0), sa(0), isa(0)
		{
			assert(args[0] >= 0);
			assert(n <= (1u << 20 << args[0]));
			assert(args[1] >= 1 && args[1] <= 7 && args[1] != 4);
			assert(level >= 1 && level <= 3);
			if ((minMatch < 4 && level == 1) || (minMatch < 1 && level == 2))
				error("match length $3 too small");

			// e8e9 transform
			if (args[1] > 4 && !sap) e8e9(inbuf.data(), n);

			// build suffix array if not supplied
			if (args[5] - args[0] >= 21 || level == 3)
			{  // LZ77-SA or BWT
				if (sap)
					sa = sap;
				else
				{
					assert(ht.size() >= n);
					assert(ht.size() > 0);
					sa = &ht[0];
					if (n > 0) divsufsort((const unsigned char*)in, (int*)sa, n);
				}
				if (level < 3)
				{
					assert(ht.size() >= (n * (sap == 0)) + (1u << 17 << args[0]));
					isa = &ht[n * (sap == 0)];
				}
			}
		}

		// Encode from in to buf until end of input or buf is not empty
		void LZBuffer::fill()
		{

			// BWT
			if (level == 3)
			{
				assert(in || n == 0);
				assert(sa);
				for (; wpos < BUFSIZE && i < n + 5; ++i)
				{
					if (i == 0) put(n > 0 ? in[n - 1] : 255);
      else if (i > n) put(idx & 255), idx >>= 8;
      else if (sa[i - 1] == 0) idx = i, put(255);
      else put(in[sa[i - 1] - 1]);
			}
			return;
		}

		// LZ77: scan the input
		unsigned lit = 0;  // number of output literals pending
		const unsigned mask = (1 << checkbits) - 1;
  while (i<n && wpos*2<BUFSIZE) {

    // Search for longest match, or pick closest in case of tie
    unsigned blen = minMatch - 1;  // best match length
		unsigned bp = 0;  // pointer to best match
		unsigned blit = 0;  // literals before best match
		int bscore = 0;  // best cost

    // Look up contexts in suffix array
    if (isa) {
      if (sa[isa[i & mask]]!=i) // rebuild ISA
        for (unsigned j=0; j<n; ++j)
          if ((sa[j]&~mask)==(i&~mask))
            isa[sa[j] & mask]=j;
      for (unsigned h=0; h<=lookahead; ++h) {
        unsigned q = isa[(h + i) & mask];  // location of h+i in SA

		assert(q<n);
        if (sa[q]!=h+i) continue;
        for (int j=-1; j<=1; j+=2) {  // search backward and forward
          for (unsigned k=1; k<=bucket; ++k) {
            unsigned p;  // match to be tested
            if (q+j* k<n && (p= sa[q + j * k] - h)<i) {

			  assert(p<n);
		unsigned l, l1;  // length of match, leading literals
              for (l=h; i+l<n && l<maxMatch && in[p + l]==in[i + l]; ++l);
              for (l1=h; l1>0 && in[p+l1-1]==in[i+l1-1]; --l1);
              int score = int(l - l1) * 8 - lg(i - p) - 4 * (lit == 0 && l1 > 0) - 11;
              for (unsigned a=0; a<h; ++a) score=score*5/8;
              if (score>bscore) blen=l, bp=p, blit=l1, bscore=score;
              if (l<blen || l<minMatch || l>255) break;
            }
}
        }
        if (bscore<=0 || blen<minMatch) break;
      }
    }

    // Look up contexts in a hash table.
    // Try the longest context orders first. If a match is found, then
    // skip the lower order as a speed optimization.
    else if (level==1 || minMatch<=64) {
      if (minMatch2>0) {
        for (unsigned k=0; k<=bucket; ++k) {
          unsigned p = ht[h2 ^ k];
          if (p && (p&mask)==(in[i+3]&mask)) {
            p>>=checkbits;
            if (p<i && i+blen<=n && in[p + blen - 1]==in[i+blen-1]) {
              unsigned l;  // match length from lookahead
              for (l=lookahead; i+l<n && l<maxMatch && in[p + l]==in[i + l]; ++l);
              if (l>=minMatch2+lookahead) {
                int l1;  // length back from lookahead
                for (l1=lookahead; l1>0 && in[p+l1-1]==in[i+l1-1]; --l1);

				assert(l1>=0 && l1<=int(lookahead));
int score = int(l - l1) * 8 - lg(i - p) - 8 * (lit == 0 && l1 > 0) - 11;
                if (score>bscore) blen=l, bp=p, blit=l1, bscore=score;
              }
            }
          }
          if (blen>=128) break;
        }
      }

      // Search the lower order context
      if (!minMatch2 || blen<minMatch2) {
        for (unsigned k=0; k<=bucket; ++k) {
          unsigned p = ht[h1 ^ k];
          if (p && i+3<n && (p&mask)==(in[i+3]&mask)) {
            p>>=checkbits;
            if (p<i && i+blen<=n && in[p + blen - 1]==in[i+blen-1]) {
              unsigned l;
              for (l=0; i+l<n && l<maxMatch && in[p + l]==in[i + l]; ++l);
              int score = l * 8 - lg(i - p) - 2 * (lit > 0) - 11;
              if (score>bscore) blen=l, bp=p, blit=0, bscore=score;
            }
          }
          if (blen>=128) break;
        }
      }
    }

	// If match is long enough, then output any pending literals first,
	// and then the match. blen is the length of the match.
	assert(i>=bp);
const unsigned off = i - bp;  // offset
    if (off>0 && bscore>0
        && blen-blit>=minMatch+(level==2)* ((off>=(1<<16))+(off>=(1<<24)))) {
      lit+=blit;

	  write_literal(i+blit, lit);

	  write_match(blen-blit, off);
    }

    // Otherwise add to literal length
    else {
      blen=1;
      ++lit;
    }

    // Update index, advance blen bytes
    if (isa)
	  i+=blen;
    else {
      while (blen--) {
        if (i+minMatchBoth<n) {
          unsigned ih = ((i * 1234547) >> 19) & bucket;
const unsigned p = (i << checkbits) | (in[i+3]&mask);

		  assert(ih<=bucket);
          if (minMatch2) {
            ht[h2 ^ ih]=p;
            h2=(((h2*9)<<shift2)
                +(in[i+minMatch2+lookahead]+1)*23456789u)&(htsize-1);
          }
          ht[h1 ^ ih]=p;
          h1=(((h1*5)<<shift1)+(in[i+minMatch]+1)*123456791u)&(htsize-1);
        }
        ++i;
      }
    }

    // Write long literals to keep buf from filling up
    if (lit>=maxLiteral)

	  write_literal(i, lit);
  }

  // Write pending literals at end of input
  assert(i<=n);
  if (i==n) {

	write_literal(n, lit);

	flush();
  }
}

// Write literal sequence in[i-lit..i-1], set lit=0
void LZBuffer::write_literal(unsigned i, unsigned& lit)
{
	assert(lit >= 0);
	assert(i >= 0 && i <= n);
	assert(i >= lit);
	if (level == 1)
	{
		if (lit < 1) return;
		int ll = lg(lit);
		assert(ll >= 1 && ll <= 24);
		putb(0, 2);
		--ll;
		while (--ll >= 0)
		{
			putb(1, 1);
			putb((lit >> ll) & 1, 1);
		}
		putb(0, 1);
		while (lit) putb(in[i - lit--], 8);
	}
	else
	{
		assert(level == 2);
		while (lit > 0)
		{
			unsigned lit1 = lit;
			if (lit1 > 64) lit1 = 64;
			put(lit1 - 1);
			for (unsigned j = i - lit; j < i - lit + lit1; ++j) put(in[j]);
			lit -= lit1;
		}
	}
}

// Write match sequence of given length and offset
void LZBuffer::write_match(unsigned len, unsigned off)
{

	// mm,mmm,n,ll,r,q[mmmmm-8] = match n*4+ll, offset ((q-1)<<rb)+r+1
	if (level == 1)
	{
		assert(len >= minMatch && len <= maxMatch);
		assert(off > 0);
		assert(len >= 4);
		assert(rb >= 0 && rb <= 8);
		int ll = lg(len) - 1;
		assert(ll >= 2);
		off += (1 << rb) - 1;
		int lo = lg(off) - 1 - rb;
		assert(lo >= 0 && lo <= 23);
		putb((lo + 8) >> 3, 2);// mm
		putb(lo & 7, 3);     // mmm
		while (--ll >= 2)
		{  // n
			putb(1, 1);
			putb((len >> ll) & 1, 1);
		}
		putb(0, 1);
		putb(len & 3, 2);    // ll
		putb(off, rb);     // r
		putb(off >> rb, lo); // q
	}

	// x[2]:len[6] off[x-1] 
	else
	{
		assert(level == 2);
		assert(minMatch >= 1 && minMatch <= 64);
		--off;
		while (len > 0)
		{  // Split long matches to len1=minMatch..minMatch+63
			const unsigned len1 = len > minMatch * 2 + 63 ? minMatch + 63 :
				len > minMatch + 63 ? len - minMatch : len;
			assert(wpos < BUFSIZE - 5);
			assert(len1 >= minMatch && len1 < minMatch + 64);
			if (off < (1 << 16))
			{
				put(64 + len1 - minMatch);
				put(off >> 8);
				put(off);
			}
			else if (off < (1 << 24))
			{
				put(128 + len1 - minMatch);
				put(off >> 16);
				put(off >> 8);
				put(off);
			}
			else
			{
				put(192 + len1 - minMatch);
				put(off >> 24);
				put(off >> 16);
				put(off >> 8);
				put(off);
			}
			len -= len1;
		}
	}
}
	}
}
