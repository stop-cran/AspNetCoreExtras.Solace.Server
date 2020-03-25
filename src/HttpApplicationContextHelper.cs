using Microsoft.AspNetCore.Http;
using System;
using System.Linq.Expressions;

namespace AspNetCoreExtras.Solace.Server
{
    public static class HttpApplicationContextHelper<TContext>
    {
        private static readonly Func<TContext, HttpContext> httpContextGetter;

        static HttpApplicationContextHelper()
        {
            var parameter = Expression.Parameter(typeof(TContext));

            httpContextGetter = Expression.Lambda<Func<TContext, HttpContext>>(
                Expression.Property(
                    parameter,
                    typeof(TContext).GetProperty("HttpContext")),
                parameter).Compile();
        }

        public static HttpContext GetHttpContext(TContext context) =>
            httpContextGetter(context);
    }
}
