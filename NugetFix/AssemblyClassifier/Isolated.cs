using System;

namespace NugetFix.AssemblyClassifier
{
    internal sealed class Isolated<T> : IDisposable where T : MarshalByRefObject
    {
        private AppDomain _domain;
        internal T Value { get; private set; }

        public Isolated()
        {
            Value = null;
            _domain = AppDomain.CreateDomain("Isolated:" + Guid.NewGuid(),
                                             null, AppDomain.CurrentDomain.SetupInformation);

            var type = typeof (T);

            if (type.FullName != null)
            {
                Value = (T) _domain.CreateInstanceAndUnwrap(type.Assembly.FullName, type.FullName);
            }
        }

        public void Dispose()
        {
            if (_domain == null) return;
            AppDomain.Unload(_domain);
            _domain = null;
        }
    }
}
