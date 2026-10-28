# Extensions panel closes immediately: optional workaround

[Back to README](../README.md)

## When this may help

After selecting **☰ → Extensions**, the application menu closes and the extensions panel fails to stay open. The puzzle-piece button may briefly appear in the address toolbar while the toolbar itself remains visible.

This workaround was confirmed by the affected user on **2026-10-27**, using **Firefox 158.0.1 for Windows** with Ghost UI and `extensions.unifiedExtensions.button.always_visible = false`. It is a possible solution for this symptom, not a general fix for every extension-popup problem.

The earlier toolbar checks exercised the direct extensions button and the **Extensions and themes → about:addons** route. They did not establish coverage of this menu-to-popup transition with an automatically hidden extensions button.

## Apply

1. Open `about:support`, then use **Profile Folder → Open Folder** to find the profile actually in use.
2. Close Firefox normally.
3. Back up that profile's `chrome/userChrome.css`.
4. Append this rule at the end of `chrome/userChrome.css`, after existing rules or imports:

```css
:root[windowtype="navigator:browser"] #navigator-toolbox #nav-bar
#nav-bar-customization-target > #unified-extensions-button:not([hidden]) {
  visibility: visible !important;
}
```

5. Start Firefox and try **☰ → Extensions** again.

The rule preserves CSS visibility for this native popup anchor while Firefox makes the button available. Existing Ghost UI opacity and pointer-event rules still control whether the hidden toolbar can be seen or clicked. The selector also retains Firefox's native `hidden` behavior.

The suspected cause is a timing interaction between Firefox temporarily revealing the anchor and Ghost UI's visibility animation. User confirmation supports the workaround in the affected profile; it does not prove that every similar failure has the same cause. A separate automated browser regression run for this workaround was not completed.

## Undo, update, or uninstall

Close Firefox, remove the added rule, then restart. Alternatively, restore the backup if no later CSS edits need preserving.

Ghost UI's installer deliberately stops when managed files have been manually edited. If update or uninstall reports that `userChrome.css` was modified, remove this addition or restore its immediate pre-workaround backup first. Keep a copy of any other edits you intend to retain.

For immediate access to installed extensions, **Ctrl+Shift+A** opens Firefox's add-ons manager.
