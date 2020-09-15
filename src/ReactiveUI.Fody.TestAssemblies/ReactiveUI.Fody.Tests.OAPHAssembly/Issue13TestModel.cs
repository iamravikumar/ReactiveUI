﻿// Copyright (c) 2020 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text;

using ReactiveUI.Fody.Helpers;

namespace ReactiveUI.Fody.Tests
{
    public class Issue13TestModel : ReactiveObject
    {
        public Issue13TestModel()
        {
            P1 = Observable.Return(0.0).ToFodyProperty();
            P2 = this.WhenAnyValue(vm => vm.P1).ToFodyProperty();
        }

        public double P1 { get; private set; }

        public double P2 { get; private set; }
    }
}
