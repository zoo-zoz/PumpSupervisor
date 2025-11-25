using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PumpSupervisor.Application.Services
{
    public interface IParameterValueTracker
    {
        bool TryGetLastValue(string key, out object? value);

        void UpdateValue(string key, object value);
    }

    public class ParameterValueTracker : IParameterValueTracker
    {
        private readonly ConcurrentDictionary<string, object> _lastValues = new();

        public bool TryGetLastValue(string key, out object? value)
        {
            return _lastValues.TryGetValue(key, out value);
        }

        public void UpdateValue(string key, object value)
        {
            _lastValues[key] = value;
        }
    }
}