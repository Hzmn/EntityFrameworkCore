﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Microsoft.EntityFrameworkCore.TestUtilities.QueryTestGeneration
{
    public class InjectOrderByPropertyExpressionMutator : ExpressionMutator
    {
        private ExpressionFinder _expressionFinder = new ExpressionFinder();

        public override bool IsValid(Expression expression)
        {
            _expressionFinder.Visit(expression);

            return _expressionFinder.FoundExpressions.Any();
        }

        public override Expression Apply(Expression expression, Random random)
        {
            var i = random.Next(_expressionFinder.FoundExpressions.Count);
            var expressionToInject = _expressionFinder.FoundExpressions.ToList()[i].Key;
            var j = random.Next(_expressionFinder.FoundExpressions[expressionToInject].Count);
            var property = _expressionFinder.FoundExpressions[expressionToInject][j];

            var typeArgument = expressionToInject.Type.GetGenericArguments()[0];

            var isDescending = random.Next(3) == 0;
            var orderBy = isDescending
                ? OrderByDescendingMethodInfo.MakeGenericMethod(typeArgument, property.PropertyType)
                : OrderByMethodInfo.MakeGenericMethod(typeArgument, property.PropertyType);

            var prm = Expression.Parameter(typeArgument, "prm");
            var lambdaBody = (Expression)Expression.Property(prm, property);

            if (property.PropertyType.IsValueType
                && !(property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>)))
            {
                var nullablePropertyType = typeof(Nullable<>).MakeGenericType(property.PropertyType);

                orderBy = isDescending
                    ? OrderByDescendingMethodInfo.MakeGenericMethod(typeArgument, nullablePropertyType)
                    : OrderByMethodInfo.MakeGenericMethod(typeArgument, nullablePropertyType);

                lambdaBody = Expression.Convert(lambdaBody, nullablePropertyType);
            }

            if (typeArgument == typeof(string))
            {
                // string.Length - make it nullable in case we access optional argument
                orderBy = OrderByMethodInfo.MakeGenericMethod(typeArgument, typeof(int?));
                lambdaBody = Expression.Convert(lambdaBody, typeof(int?));
            }

            var lambda = Expression.Lambda(lambdaBody, prm);
            var injector = new ExpressionInjector(expressionToInject, e => Expression.Call(orderBy, e, lambda));

            return injector.Visit(expression);
        }

        private class ExpressionFinder : ExpressionVisitor
        {
            private List<PropertyInfo> GetValidPropertiesForOrderBy(Expression expression)
                => expression.Type.GetGenericArguments()[0].GetProperties().Where(p => !p.GetMethod.IsStatic)
                    .Where(p => p.PropertyType.GetInterfaces().Any(i => i == typeof(IComparable))).ToList();

            public Dictionary<Expression, List<PropertyInfo>> FoundExpressions { get; set; } = new Dictionary<Expression, List<PropertyInfo>>();

            private bool _insideThenInclude = false;

            public override Expression Visit(Expression expression)
            {
                // can't inject OrderBy inside of include - would have to rewrite the ThenInclude method to one that accepts ordered input
                var insideThenInclude = default(bool?);
                if (expression is MethodCallExpression methodCallExpression
                    && (methodCallExpression.Method.Name == "ThenInclude" || methodCallExpression.Method.Name == "ThenIncludeDescending"))
                {
                    insideThenInclude = _insideThenInclude;
                    _insideThenInclude = true;
                }

                if (!_insideThenInclude
                    && expression != null
                    && IsQueryableResult(expression)
                    && !FoundExpressions.ContainsKey(expression))
                {
                    var validProperties = GetValidPropertiesForOrderBy(expression);
                    validProperties = FilterPropertyInfos(expression.Type.GetGenericArguments()[0], validProperties);

                    if (validProperties.Any())
                    {
                        FoundExpressions.Add(expression, validProperties);
                    }
                }

                try
                {
                    return base.Visit(expression);
                }
                finally
                {
                    _insideThenInclude = insideThenInclude ?? _insideThenInclude;
                }
            }
        }
    }
}