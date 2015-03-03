// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNet.Mvc.Core;
using Microsoft.AspNet.Mvc.ModelBinding;
using Microsoft.Framework.OptionsModel;

namespace Microsoft.AspNet.Mvc
{
    /// <summary>
    /// Provides a default implementation of <see cref="IControllerActionArgumentBinder"/>.
    /// Uses ModelBinding to populate action parameters.
    /// </summary>
    public class DefaultControllerActionArgumentBinder : IControllerActionArgumentBinder
    {
        private readonly IModelMetadataProvider _modelMetadataProvider;
        private readonly MvcOptions _options;
        private readonly IObjectModelValidator _validator;

        public DefaultControllerActionArgumentBinder(
            IModelMetadataProvider modelMetadataProvider,
            IObjectModelValidator validator,
            IOptions<MvcOptions> optionsAccessor)
        {
            _modelMetadataProvider = modelMetadataProvider;
            _options = optionsAccessor.Options;
            _validator = validator;
        }

        public async Task<IDictionary<string, object>> GetActionArgumentsAsync(
            ActionContext actionContext,
            ActionBindingContext actionBindingContext, 
            object controller)
        {
            var actionDescriptor = actionContext.ActionDescriptor as ControllerActionDescriptor;
            if (actionDescriptor == null)
            {
                throw new ArgumentException(
                    Resources.FormatActionDescriptorMustBeBasedOnControllerAction(
                        typeof(ControllerActionDescriptor)),
                        nameof(actionContext));
            }

            var parameterMetadata = new List<ModelMetadata>();
            foreach (var parameter in actionDescriptor.Parameters)
            {
                var metadata = _modelMetadataProvider.GetMetadataForParameter(
                    methodInfo: actionDescriptor.MethodInfo,
                    parameterName: parameter.Name);

                if (metadata != null)
                {
                    UpdateParameterMetadata(metadata, parameter.BinderMetadata);
                    parameterMetadata.Add(metadata);
                }
            }

            var controllerType = actionDescriptor.ControllerTypeInfo.AsType();
            var propertyMetadata = new List<ModelMetadata>();
            foreach (var property in actionDescriptor.CommonParameters)
            {
                var metadata = _modelMetadataProvider.GetMetadataForProperty(
                    controllerType,
                    propertyName: property.Name);

                if (metadata != null)
                {
                    UpdateParameterMetadata(metadata, property.BinderMetadata);
                    propertyMetadata.Add(metadata);
                }
            }

            var operationBindingContext = GetOperationBindingContext(actionContext, actionBindingContext);
           
            var controllerProperties = new Dictionary<string, object>(StringComparer.Ordinal);
            await PopulateArgumentAsync(
                operationBindingContext,
                actionContext.ModelState,
                controllerProperties,
                propertyMetadata);
            ActivateProperties(controller, controllerType, controllerProperties);

            var actionArguments = new Dictionary<string, object>(StringComparer.Ordinal);
            await PopulateArgumentAsync(
                operationBindingContext,
                actionContext.ModelState,
                actionArguments,
                parameterMetadata);
            return actionArguments;
        }

        // Internal for tests
        internal static ModelBindingContext GetModelBindingContext(
            ModelMetadata modelMetadata,
            ModelStateDictionary modelState,
            OperationBindingContext operationBindingContext)
        {
            var modelBindingContext = new ModelBindingContext
            {
                ModelName = modelMetadata.BinderModelName ?? modelMetadata.PropertyName,
                ModelMetadata = modelMetadata,
                ModelState = modelState,

                // Fallback only if there is no explicit model name set.
                FallbackToEmptyPrefix = modelMetadata.BinderModelName == null,
                ValueProvider = operationBindingContext.ValueProvider,
                OperationBindingContext = operationBindingContext,
            };

            return modelBindingContext;
        }

        private void ActivateProperties(object controller, Type containerType, Dictionary<string, object> properties)
        {
            foreach (var property in properties)
            {
                SetProperty(property.Key, containerType, controller, property.Value);
            }
        }

        private void SetProperty(string propertyName, Type containerType, object container, object value)
        {
            var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            var property = containerType.GetProperty(propertyName, bindingFlags);
            if (property == null || !property.CanWrite)
            {
                // nothing to do
                return;
            }

            property.SetValue(container, value);
        }

        private void UpdateParameterMetadata(ModelMetadata metadata, IBinderMetadata binderMetadata)
        {
            if (binderMetadata != null)
            {
                metadata.BinderMetadata = binderMetadata;
            }

            var nameProvider = binderMetadata as IModelNameProvider;
            if (nameProvider != null && nameProvider.Name != null)
            {
                metadata.BinderModelName = nameProvider.Name;
            }
        }

        private async Task PopulateArgumentAsync(
            OperationBindingContext operationContext,
            ModelStateDictionary modelState,
            IDictionary<string, object> arguments,
            IEnumerable<ModelMetadata> parameterMetadata)
        {
            modelState.MaxAllowedErrors = _options.MaxModelValidationErrors;
            foreach (var parameter in parameterMetadata)
            {
                var parameterType = parameter.ModelType;

                var modelBindingContext = GetModelBindingContext(parameter, modelState, operationContext);
                var modelBindingResult = await operationContext.ModelBinder.BindModelAsync(modelBindingContext);
                if (modelBindingResult != null && modelBindingResult.IsModelSet)
                {
                    var modelExplorer = new ModelExplorer(_modelMetadataProvider, parameter, modelBindingResult.Model);

                    arguments[parameter.PropertyName] = modelBindingResult.Model;
                    var validationContext = new ModelValidationContext(
                        modelBindingResult.Key,
                        operationContext.ValidatorProvider,
                        modelState,
                        modelExplorer);
                    _validator.Validate(validationContext);
                }
            }
        }

        private OperationBindingContext GetOperationBindingContext(
            ActionContext actionContext,
            ActionBindingContext bindingContext)
        {
            return new OperationBindingContext
            {
                ModelBinder = bindingContext.ModelBinder,
                ValidatorProvider = bindingContext.ValidatorProvider,
                MetadataProvider = _modelMetadataProvider,
                HttpContext = actionContext.HttpContext,
                ValueProvider = bindingContext.ValueProvider,
            };
        }
    }
}
