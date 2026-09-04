/*
    Copyright (C) 2026 netSpy contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace dnSpy.MainApp {
	sealed partial class BrandMark : UserControl {
		public BrandMark() => InitializeComponent();
	}

	sealed class DecorativeAccent : Border {
		protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeAccentAutomationPeer(this);

		sealed class DecorativeAccentAutomationPeer : FrameworkElementAutomationPeer {
			public DecorativeAccentAutomationPeer(DecorativeAccent owner) : base(owner) { }

			protected override bool IsControlElementCore() => false;
			protected override bool IsContentElementCore() => false;
		}
	}
}
