using System;
using Autodesk.Revit.UI;

namespace HD.Core.Helpers
{
    /// <summary>
    /// Helper for ExternalEvent pattern - ensures Revit API calls run on main thread.
    /// </summary>
    public abstract class ExternalEventHandlerBase : IExternalEventHandler
    {
        public abstract void Execute(UIApplication app);

        public virtual string GetName() => GetType().Name;
    }

    /// <summary>
    /// Simple request-based external event handler.
    /// </summary>
    /// <typeparam name="TRequest">Request type</typeparam>
    public abstract class RequestHandler<TRequest> : IExternalEventHandler where TRequest : class
    {
        private TRequest _request;
        private readonly object _lock = new();

        public void SetRequest(TRequest request)
        {
            lock (_lock) { _request = request; }
        }

        public void Execute(UIApplication app)
        {
            TRequest request;
            lock (_lock) { request = _request; _request = null; }
            if (request == null) return;

            try
            {
                HandleRequest(app, request);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        protected abstract void HandleRequest(UIApplication app, TRequest request);

        protected virtual void OnError(Exception ex)
        {
            // Override to handle errors
        }

        public string GetName() => GetType().Name;
    }
}
