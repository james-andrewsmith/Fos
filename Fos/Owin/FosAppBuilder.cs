using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Owin;
using System.Linq;
using System.Linq.Expressions;

namespace Fos.Owin
{
    using AppFunc = Func<IDictionary<string, object>, Task>;


    internal class NotFound
    {
        private static readonly Task Completed = CreateCompletedTask();

        private static Task CreateCompletedTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetResult(null);
            return tcs.Task;
        }

        public Task Invoke(IDictionary<string, object> env)
        {
            env["owin.ResponseStatusCode"] = 404;
            return Completed;
        }
    }

	internal class FosAppBuilder : IAppBuilder
	{
		private Dictionary<string, object> properties;

		public CancellationToken OnAppDisposing { get; private set; }

		private FosOwinRoot RootMiddleware;

		/// <summary>
		/// This is the last middleware added through <see cref="Use"/>, or the <see cref="RootMiddleWare"/> in case <see cref="Use"/> has not been called yet.
		/// </summary>
		private OwinMiddleware LastMiddleware;

		public FosAppBuilder(CancellationToken cancelToken)
		{
            properties = new Dictionary<string, object>();
            
            _conversions = new Dictionary<Tuple<Type, Type>, Delegate>();
            _middleware = new List<Tuple<Type, Delegate, object[]>>();

			RootMiddleware = new FosOwinRoot();

			//WARN: Non standard Owin header. Used by Nancy
			OnAppDisposing = cancelToken;
			properties.Add("host.OnAppDisposing", cancelToken);

            //HACK: Microsoft Middleware self defeating OWIN implementation fix
            properties["builder.AddSignatureConversion"] = new Action<Delegate>(AddSignatureConversion);

		}

        #region // Owin Conversion Delegates //

        private readonly IDictionary<Tuple<Type, Type>, Delegate> _conversions;
        private readonly IList<Tuple<Type, Delegate, object[]>> _middleware;

        private void AddSignatureConversion(Delegate conversion)
        {
            if (conversion == null)
            {
                throw new ArgumentNullException("conversion");
            }

            Type parameterType = GetParameterType(conversion);
            if (parameterType == null)
            {
                throw new ArgumentException("", "conversion");
            }
            Tuple<Type, Type> key = Tuple.Create(conversion.Method.ReturnType, parameterType);
            _conversions[key] = conversion;
        }

        private static Type GetParameterType(Delegate function)
        {
            ParameterInfo[] parameters = function.Method.GetParameters();
            return parameters.Length == 1 ? parameters[0].ParameterType : null;
        }

        private static readonly AppFunc NotFound = new NotFound().Invoke;

        private object BuildInternal(Type signature)
        {
            object app;
            if (!properties.TryGetValue("builder.DefaultApp", out app))
            {
                app = NotFound;
            }

            foreach (Tuple<Type, Delegate, object[]> middleware in _middleware.Reverse())
            {
                Type neededSignature = middleware.Item1;
                Delegate middlewareDelegate = middleware.Item2;
                object[] middlewareArgs = middleware.Item3;

                app = Convert(neededSignature, app);
                object[] invokeParameters = new[] { app }.Concat(middlewareArgs).ToArray();
                app = middlewareDelegate.DynamicInvoke(invokeParameters);
                app = Convert(neededSignature, app);
            }

            return Convert(signature, app);
        }

        private object Convert(Type signature, object app)
        {
            if (app == null)
            {
                return null;
            }

            object oneHop = ConvertOneHop(signature, app);
            if (oneHop != null)
            {
                return oneHop;
            }

            object multiHop = ConvertMultiHop(signature, app);
            if (multiHop != null)
            {
                return multiHop;
            }
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, 
                                                      "Exception_NoConversionExists {0}:{1}", app.GetType(), signature), 
                                                      "signature");
        }

        private object ConvertMultiHop(Type signature, object app)
        {
            foreach (KeyValuePair<Tuple<Type, Type>, Delegate> conversion in _conversions)
            {
                object preConversion = ConvertOneHop(conversion.Key.Item2, app);
                if (preConversion == null)
                {
                    continue;
                }
                object intermediate = conversion.Value.DynamicInvoke(preConversion);
                if (intermediate == null)
                {
                    continue;
                }
                object postConversion = ConvertOneHop(signature, intermediate);
                if (postConversion == null)
                {
                    continue;
                }

                return postConversion;
            }
            return null;
        }

        private object ConvertOneHop(Type signature, object app)
        {
            if (signature.IsInstanceOfType(app))
            {
                return app;
            }
            if (typeof(Delegate).IsAssignableFrom(signature))
            {
                Delegate memberDelegate = ToMemberDelegate(signature, app);
                if (memberDelegate != null)
                {
                    return memberDelegate;
                }
            }
            foreach (KeyValuePair<Tuple<Type, Type>, Delegate> conversion in _conversions)
            {
                Type returnType = conversion.Key.Item1;
                Type parameterType = conversion.Key.Item2;
                if (parameterType.IsInstanceOfType(app) &&
                    signature.IsAssignableFrom(returnType))
                {
                    return conversion.Value.DynamicInvoke(app);
                }
            }
            return null;
        }

        private static Delegate ToMemberDelegate(Type signature, object app)
        {
            MethodInfo signatureMethod = signature.GetMethod("Invoke");
            ParameterInfo[] signatureParameters = signatureMethod.GetParameters();

            MethodInfo[] methods = app.GetType().GetMethods();
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "Invoke")
                {
                    continue;
                }
                ParameterInfo[] methodParameters = method.GetParameters();
                if (methodParameters.Length != signatureParameters.Length)
                {
                    continue;
                }
                if (methodParameters.Zip(signatureParameters, (methodParameter, signatureParameter) => methodParameter.ParameterType.IsAssignableFrom(signatureParameter.ParameterType))
                                    .Any(compatible => compatible == false))
                {
                    continue;
                }
                if (!signatureMethod.ReturnType.IsAssignableFrom(method.ReturnType))
                {
                    continue;
                }
                return Delegate.CreateDelegate(signature, app, method);
            }
            return null;
        }

        // [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "False positive")]
        private static Tuple<Type, Delegate, object[]> ToMiddlewareFactory(object middlewareObject, object[] args)
        {
            if (middlewareObject == null)
            {
                throw new ArgumentNullException("middlewareObject");
            }

            Delegate middlewareDelegate = middlewareObject as Delegate;
            if (middlewareDelegate != null)
            {
                return Tuple.Create(GetParameterType(middlewareDelegate), middlewareDelegate, args);
            }

            Tuple<Type, Delegate, object[]> factory = ToInstanceMiddlewareFactory(middlewareObject, args);
            if (factory != null)
            {
                return factory;
            }

            factory = ToGeneratorMiddlewareFactory(middlewareObject, args);
            if (factory != null)
            {
                return factory;
            }

            if (middlewareObject is Type)
            {
                return ToConstructorMiddlewareFactory(middlewareObject, args, ref middlewareDelegate);
            }

            throw new NotSupportedException("");
        }

        // Instance pattern: public void Initialize(AppFunc next, string arg1, string arg2), public Task Invoke(IDictionary<...> env)
        private static Tuple<Type, Delegate, object[]> ToInstanceMiddlewareFactory(object middlewareObject, object[] args)
        {
            MethodInfo[] methods = middlewareObject.GetType().GetMethods();
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "Initialize")
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

                if (parameterTypes.Length != args.Length + 1)
                {
                    continue;
                }
                if (!parameterTypes
                    .Skip(1)
                    .Zip(args, TestArgForParameter)
                    .All(x => x))
                {
                    continue;
                }

                // DynamicInvoke can't handle a middleware with multiple args, just push the args in via closure.
                Func<object, object> func = app =>
                {
                    object[] invokeParameters = new[] { app }.Concat(args).ToArray();
                    method.Invoke(middlewareObject, invokeParameters);
                    return middlewareObject;
                };

                return Tuple.Create<Type, Delegate, object[]>(parameters[0].ParameterType, func, new object[0]);
            }
            return null;
        }

        // Delegate nesting pattern: public AppFunc Invoke(AppFunc app, string arg1, string arg2)
        private static Tuple<Type, Delegate, object[]> ToGeneratorMiddlewareFactory(object middlewareObject, object[] args)
        {
            MethodInfo[] methods = middlewareObject.GetType().GetMethods();
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "Invoke")
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

                if (parameterTypes.Length != args.Length + 1)
                {
                    continue;
                }
                if (!parameterTypes
                    .Skip(1)
                    .Zip(args, TestArgForParameter)
                    .All(x => x))
                {
                    continue;
                }
                IEnumerable<Type> genericFuncTypes = parameterTypes.Concat(new[] { method.ReturnType });
                Type funcType = Expression.GetFuncType(genericFuncTypes.ToArray());
                Delegate middlewareDelegate = Delegate.CreateDelegate(funcType, middlewareObject, method);
                return Tuple.Create(parameters[0].ParameterType, middlewareDelegate, args);
            }
            return null;
        }

        // Type Constructor pattern: public Delta(AppFunc app, string arg1, string arg2)
        private static Tuple<Type, Delegate, object[]> ToConstructorMiddlewareFactory(object middlewareObject, object[] args, ref Delegate middlewareDelegate)
        {
            Type middlewareType = middlewareObject as Type;
            ConstructorInfo[] constructors = middlewareType.GetConstructors();
            foreach (ConstructorInfo constructor in constructors)
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();
                if (parameterTypes.Length != args.Length + 1)
                {
                    continue;
                }
                if (!parameterTypes
                    .Skip(1)
                    .Zip(args, TestArgForParameter)
                    .All(x => x))
                {
                    continue;
                }

                ParameterExpression[] parameterExpressions = parameters.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                NewExpression callConstructor = Expression.New(constructor, parameterExpressions);
                middlewareDelegate = Expression.Lambda(callConstructor, parameterExpressions).Compile();
                return Tuple.Create(parameters[0].ParameterType, middlewareDelegate, args);
            }

            throw new MissingMethodException(middlewareType.FullName,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, 
                              "{0}", args.Length + 1));
        }

        private static bool TestArgForParameter(Type parameterType, object arg)
        {
            return (arg == null && !parameterType.IsValueType) ||
                parameterType.IsInstanceOfType(arg);
        }

        #endregion 


        public IAppBuilder Use(object middleware, params object[] args)
		{
            _middleware.Add(ToMiddlewareFactory(middleware, args));
            return this;

			Delegate delegateMiddleware = middleware as Delegate;
			OwinMiddleware newMiddleware;
			if (delegateMiddleware != null)
			{
				newMiddleware = new OwinMiddleware(delegateMiddleware, args);
			}
			else
			{
				Type typeMiddleware = middleware as Type;

				if (typeMiddleware != null)
					newMiddleware = new OwinMiddleware(typeMiddleware, args);
				else
					throw new ArgumentException("The middleware to be used needs either to be a Type or a Delegate");
			}

			// Update the chain of middleware
			if (LastMiddleware == null)
				RootMiddleware.Next = newMiddleware;
			else
				LastMiddleware.Next = newMiddleware;

			LastMiddleware = newMiddleware;

			return this;
		}

		public object Build (Type returnType)
		{
            return BuildInternal(returnType);

			// if (returnType == typeof(Func<IDictionary<string, object>, Task>))
			// {
			// 	return (Func<IDictionary<string, object>, Task>)RootMiddleware.Invoke;
			// }
			// else
			// 	throw new NotSupportedException("Only Func<IDictionary<string, object>, Task> is supported right now");
		}

		public IAppBuilder New ()
		{
			return new FosAppBuilder(OnAppDisposing);
		}

		public IDictionary<string, object> Properties {
			get
            {
				return properties;
			}
		}


         
	}
}
