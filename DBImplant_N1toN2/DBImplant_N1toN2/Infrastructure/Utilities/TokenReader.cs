using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Infrastructure.Utilities
{
    internal class TokenReader : IDisposable
    {
        private readonly TextReader _r;
        private int _peek = -2; // -2 = empty, -1 = EOF

        public TokenReader(TextReader r) => _r = r;

        public string NextToken()
        {
            int ch = Read();
            while (ch != -1 && char.IsWhiteSpace((char)ch))
                ch = Read();

            if (ch == -1)
                throw new EndOfStreamException("Unexpected EOF while reading .set");

            var sb = new StringBuilder();
            while (ch != -1 && !char.IsWhiteSpace((char)ch))
            {
                sb.Append((char)ch);
                ch = Read();
            }
            return sb.ToString();
        }

        public int NextInt()
            => int.TryParse(NextToken(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        public double NextDouble()
            => double.TryParse(NextToken(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

        private int Read()
        {
            if (_peek != -2)
            {
                int tmp = _peek;
                _peek = -2;
                return tmp;
            }
            return _r.Read();
        }

        public void Dispose()
        {
            if (_r is IDisposable d) d.Dispose();
        }
    }
}
