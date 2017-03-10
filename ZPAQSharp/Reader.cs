namespace ZPAQSharp
{
	// Virtual base classes for input and output
	// get() and put() must be overridden to read or write 1 byte.
	// read() and write() may be overridden to read or write n bytes more
	// efficiently than calling get() or put() n times.
	class Reader
	{
		public virtual int get() // should return 0..255, or -1 at EOF
		{
			return 0;
		}

		public virtual int read(char[] buf, int n) // read to buf[n], return no. read
		{
			int i = 0, c;
			while (i < n && (c = get()) >= 0)
			{
				buf[i++] = (char)c;
			}
				
			return i;
		}

		~Reader()
		{
		}
	}
}
