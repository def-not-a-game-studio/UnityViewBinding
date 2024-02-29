# Automatic View Binding for Unity

Thank you for supporting me and buying this asset. I hope you find it as useful as I did when creating it.

## Getting Started

After you create your UI prefab structure, be mindful of a few constraints:

- Each element/GameObject name will be treated as a unique id, meaning there can be only one with the same name and type
- The UI prefab name _must_ end in ".View"
- The associated MonoBehaviour must have only letters in its name _and have a "Binding" suffix_, so for a UI prefab named "CustomHeader.View" the MonoBehaviour should be "CustomHeaderViewBinding"
- The associated MonoBehaviour must be a partial class
- The changes to the prefab will only be picked up if made from the prefab stage view (when you double click a prefab to edit it)

## Contact Info

- Discord: danilov3s
- Twitter: https://twitter.com/Danil0v3s
- GitHub: https://github.com/Danil0v3s