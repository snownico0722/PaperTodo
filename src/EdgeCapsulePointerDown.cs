namespace PaperTodo;

// The position and modifier state belong to the original native message, not to the later
// handoff callback. ScreenPoint is converted once, while the output HWND is still unchanged.
internal readonly record struct EdgeCapsulePointerDown(
    DeviceScreenPoint ScreenPoint, int Message, IntPtr KeyState);
