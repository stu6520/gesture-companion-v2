#include <windows.h>

#include <algorithm>
#include <atomic>
#include <array>
#include <chrono>
#include <cmath>
#include <cctype>
#include <deque>
#include <initializer_list>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

namespace
{
constexpr UINT WM_POINTERUPDATE_VALUE = 0x0245;
constexpr UINT WM_POINTERDOWN_VALUE = 0x0246;
constexpr UINT WM_POINTERUP_VALUE = 0x0247;
constexpr double TapMovementPixels = 35.0;
constexpr auto TapDuration = std::chrono::milliseconds(700);
constexpr double OneFingerSlidePixels = 40.0;
constexpr double OneFingerHoldMovementPixels = 15.0;
constexpr UINT OneFingerHoldMilliseconds = 600;
constexpr int PanDebugGesture = -2;

enum class Gesture
{
    PinchOpen,
    PinchClose,
    RotateClockwise,
    RotateCounterClockwise,
    OneFingerSlide,
    OneFingerHold,

    TwoFingerTap,
    ThreeFingerTap,
    FourFingerTap
};


struct PointD
{
    double x = 0;
    double y = 0;
};

struct TouchSession
{
    bool active = false;
    bool fired = false;
    bool panDragActive = false;
    HWND panKeyboardTarget = nullptr;
    HWND panMouseTarget = nullptr;
    std::vector<WORD> panKeys;
    bool oneFingerKeyHoldActive = false;
    bool oneFingerMouseHoldActive = false;
    std::vector<WORD> oneFingerHeldKeys;
    PointD panLastPoint{};
    int peakContacts = 0;
    double maxMovement = 0;
    bool pairValid = false;
    UINT32 pairFirst = 0;
    UINT32 pairSecond = 0;
    PointD pairCentroid{};
    double pairDistance = 0;
    double pairAngle = 0;
    std::chrono::steady_clock::time_point started{};
    double panAccumulatorX = 0;
    double panAccumulatorY = 0;
    double zoomAccumulator = 0;
    double rotateAccumulator = 0;

    std::unordered_map<UINT32, PointD> firstPositions;
};

HMODULE g_module = nullptr;
std::vector<HHOOK> g_pointerHooks;
std::map<UINT32, PointD> g_contacts;
TouchSession g_session;
std::mutex g_bindingsMutex;
std::map<Gesture, std::vector<WORD>> g_bindings;
std::vector<WORD> g_panBinding{ VK_SPACE };
bool g_oneFingerSlideUsesPan = false;
bool g_oneFingerHoldUsesRightClick = false;
double g_zoomStepPixels = 45.0;
double g_rotateStepDegrees = 12.0;
double g_panStepPixels = 55.0;
DWORD g_zoomIntervalMilliseconds = 0;
DWORD g_rotateIntervalMilliseconds = 0;
std::atomic<ULONGLONG> g_lastZoomDispatchTick{ 0 };
std::atomic<ULONGLONG> g_lastRotateDispatchTick{ 0 };
std::atomic<int> g_debugContacts{ 0 };
std::atomic<int> g_debugPeakContacts{ 0 };
std::atomic<int> g_debugPanX100{ 0 };
std::atomic<int> g_debugPanY100{ 0 };
std::atomic<int> g_debugZoom100{ 0 };
std::atomic<int> g_debugRotate100{ 0 };
std::atomic<int> g_debugMovement100{ 0 };
std::atomic<int> g_debugLastGesture{ -1 };
std::atomic<unsigned long> g_debugEventCount{ 0 };
std::atomic<unsigned long> g_debugSessionEventCount{ 0 };
std::atomic<unsigned long> g_debugRawUpdateCount{ 0 };
std::atomic<unsigned long> g_debugEvaluatedFrameCount{ 0 };
std::atomic<unsigned long> g_debugPointerMessageCount{ 0 };
std::atomic<unsigned long> g_debugTouchMessageCount{ 0 };
std::atomic<unsigned long> g_debugPointerInfoFailureCount{ 0 };
std::atomic<bool> g_debugHookInstalled{ false };
std::atomic<bool> g_debugPanActive{ false };
std::mutex g_gestureQueueMutex;
std::deque<Gesture> g_gestureQueue;
HANDLE g_gestureDispatchEvent = nullptr;
HANDLE g_gestureDispatchThread = nullptr;
std::atomic<bool> g_gestureDispatchStopping{ false };
UINT_PTR g_oneFingerHoldTimer = 0;

void CancelOneFingerHoldTimer()
{
    if (g_oneFingerHoldTimer != 0)
    {
        KillTimer(nullptr, g_oneFingerHoldTimer);
        g_oneFingerHoldTimer = 0;
    }
}

bool QueueGestureDispatch(Gesture gesture)
{
    if (g_gestureDispatchEvent == nullptr || g_gestureDispatchStopping.load())
    {
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(g_gestureQueueMutex);
        g_gestureQueue.push_back(gesture);
    }
    SetEvent(g_gestureDispatchEvent);
    return true;
}

void RecordDebugGesture(int gesture)
{
    g_debugLastGesture.store(gesture, std::memory_order_relaxed);
    g_debugEventCount.fetch_add(1, std::memory_order_relaxed);
    g_debugSessionEventCount.fetch_add(1, std::memory_order_relaxed);
}

void UpdateDebugSnapshot()
{
    g_debugContacts.store(static_cast<int>(g_contacts.size()), std::memory_order_relaxed);
    g_debugPeakContacts.store(g_session.peakContacts, std::memory_order_relaxed);
    g_debugPanX100.store(static_cast<int>(std::lround(g_session.panAccumulatorX * 100.0)), std::memory_order_relaxed);
    g_debugPanY100.store(static_cast<int>(std::lround(g_session.panAccumulatorY * 100.0)), std::memory_order_relaxed);
    g_debugZoom100.store(static_cast<int>(std::lround(g_session.zoomAccumulator * 100.0)), std::memory_order_relaxed);
    g_debugRotate100.store(static_cast<int>(std::lround(g_session.rotateAccumulator * 100.0)), std::memory_order_relaxed);
    g_debugMovement100.store(static_cast<int>(std::lround(g_session.maxMovement * 100.0)), std::memory_order_relaxed);
    g_debugPanActive.store(g_session.panDragActive, std::memory_order_relaxed);
}

const char* DebugGestureName(int gesture)
{
    switch (gesture)
    {
    case static_cast<int>(Gesture::PinchOpen): return "pinch-open";
    case static_cast<int>(Gesture::PinchClose): return "pinch-close";
    case static_cast<int>(Gesture::RotateClockwise): return "rotate-cw";
    case static_cast<int>(Gesture::RotateCounterClockwise): return "rotate-ccw";
    case static_cast<int>(Gesture::OneFingerSlide): return "one-finger-slide";
    case static_cast<int>(Gesture::OneFingerHold): return "one-finger-hold";
    case static_cast<int>(Gesture::TwoFingerTap): return "two-finger-tap";
    case static_cast<int>(Gesture::ThreeFingerTap): return "three-finger-tap";
    case static_cast<int>(Gesture::FourFingerTap): return "four-finger-tap";
    case PanDebugGesture: return "pan-drag";
    default: return "none";
    }
}

std::string BuildDebugSnapshot()
{
    std::ostringstream stream;
    stream << "{\"state\":\"debug\",\"contacts\":" << g_debugContacts.load()
        << ",\"peak\":" << g_debugPeakContacts.load()
        << ",\"panX100\":" << g_debugPanX100.load()
        << ",\"panY100\":" << g_debugPanY100.load()
        << ",\"zoom100\":" << g_debugZoom100.load()
        << ",\"rotate100\":" << g_debugRotate100.load()
        << ",\"movement100\":" << g_debugMovement100.load()
        << ",\"panActive\":" << (g_debugPanActive.load() ? "true" : "false")
        << ",\"last\":\"" << DebugGestureName(g_debugLastGesture.load()) << "\""
        << ",\"sessionEvents\":" << g_debugSessionEventCount.load()
        << ",\"events\":" << g_debugEventCount.load()
        << ",\"rawUpdates\":" << g_debugRawUpdateCount.load()
        << ",\"evaluatedFrames\":" << g_debugEvaluatedFrameCount.load()
        << ",\"pointerMessages\":" << g_debugPointerMessageCount.load()
        << ",\"touchMessages\":" << g_debugTouchMessageCount.load()
        << ",\"pointerInfoFailures\":" << g_debugPointerInfoFailureCount.load()
        << ",\"hookInstalled\":" << (g_debugHookInstalled.load() ? "true" : "false") << "}";
    return stream.str();
}

std::wstring GetPipeName()
{
    return L"\\\\.\\pipe\\GestureCompanionBridge-" + std::to_wstring(GetCurrentProcessId());
}

bool ReadLine(HANDLE pipe, std::string& line)
{
    line.clear();
    char ch = 0;
    DWORD read = 0;
    while (ReadFile(pipe, &ch, 1, &read, nullptr) && read == 1)
    {
        if (ch == '\n')
        {
            if (!line.empty() && line.back() == '\r')
            {
                line.pop_back();
            }
            return true;
        }
        line.push_back(ch);
    }
    return !line.empty();
}

void WriteLine(HANDLE pipe, const char* text)
{
    DWORD written = 0;
    std::string line(text);
    line.push_back('\n');
    WriteFile(pipe, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
}

BOOL CALLBACK FindMainWindowProc(HWND window, LPARAM result)
{
    DWORD processId = 0;
    GetWindowThreadProcessId(window, &processId);
    if (processId == GetCurrentProcessId() &&
        IsWindowVisible(window) &&
        GetWindow(window, GW_OWNER) == nullptr)
    {
        *reinterpret_cast<HWND*>(result) = window;
        return FALSE;
    }
    return TRUE;
}

HWND FindMainWindow()
{
    HWND result = nullptr;
    EnumWindows(FindMainWindowProc, reinterpret_cast<LPARAM>(&result));
    return result;
}

HWND FindKeyboardTarget()
{
    HWND mainWindow = FindMainWindow();
    if (mainWindow == nullptr)
    {
        return nullptr;
    }

    DWORD processId = 0;
    const DWORD threadId = GetWindowThreadProcessId(mainWindow, &processId);
    GUITHREADINFO info{};
    info.cbSize = sizeof(info);

    HWND target = mainWindow;
    if (GetGUIThreadInfo(threadId, &info))
    {
        if (info.hwndFocus != nullptr)
        {
            target = info.hwndFocus;
        }
        else if (info.hwndActive != nullptr)
        {
            target = info.hwndActive;
        }
    }

    DWORD targetProcessId = 0;
    GetWindowThreadProcessId(target, &targetProcessId);
    return targetProcessId == GetCurrentProcessId() ? target : mainWindow;
}

void FocusHostWindow()
{
    HWND window = FindMainWindow();
    if (window == nullptr)
    {
        return;
    }
    if (IsIconic(window))
    {
        ShowWindow(window, SW_RESTORE);
    }
    SetForegroundWindow(window);
    SetFocus(window);
    Sleep(80);
}

void SendKeyCombo(std::initializer_list<WORD> keys)
{
    FocusHostWindow();
    std::array<INPUT, 16> inputs{};
    int index = 0;
    for (const WORD key : keys)
    {
        inputs[index].type = INPUT_KEYBOARD;
        inputs[index].ki.wVk = key;
        ++index;
    }
    for (auto it = std::rbegin(keys); it != std::rend(keys); ++it)
    {
        inputs[index].type = INPUT_KEYBOARD;
        inputs[index].ki.wVk = *it;
        inputs[index].ki.dwFlags = KEYEVENTF_KEYUP;
        ++index;
    }
    SendInput(static_cast<UINT>(index), inputs.data(), sizeof(INPUT));
}

bool IsExtendedKey(WORD key)
{
    switch (key)
    {
    case VK_MENU:
    case VK_CONTROL:
    case VK_RMENU:
    case VK_RCONTROL:
    case VK_INSERT:
    case VK_DELETE:
    case VK_HOME:
    case VK_END:
    case VK_PRIOR:
    case VK_NEXT:
    case VK_RIGHT:
    case VK_UP:
    case VK_LEFT:
    case VK_DOWN:
    case VK_NUMLOCK:
    case VK_CANCEL:
    case VK_SNAPSHOT:
    case VK_DIVIDE:
        return true;
    default:
        return false;
    }
}

bool SendKeyComboInput(const std::vector<WORD>& keys)
{
    if (keys.empty())
    {
        return false;
    }

    std::vector<INPUT> inputs(keys.size() * 2);
    for (size_t index = 0; index < keys.size(); ++index)
    {
        inputs[index].type = INPUT_KEYBOARD;
        inputs[index].ki.wVk = keys[index];
        inputs[index].ki.dwFlags = IsExtendedKey(keys[index]) ? KEYEVENTF_EXTENDEDKEY : 0;
    }
    for (size_t index = 0; index < keys.size(); ++index)
    {
        const WORD key = keys[keys.size() - 1 - index];
        INPUT& input = inputs[keys.size() + index];
        input.type = INPUT_KEYBOARD;
        input.ki.wVk = key;
        input.ki.dwFlags = KEYEVENTF_KEYUP |
            (IsExtendedKey(key) ? KEYEVENTF_EXTENDEDKEY : 0);
    }

    return SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT)) == inputs.size();
}

bool SendHeldKeyStateInput(const std::vector<WORD>& keys, bool release)
{
    if (keys.empty())
    {
        return false;
    }

    std::vector<INPUT> inputs(keys.size());
    for (size_t index = 0; index < keys.size(); ++index)
    {
        const WORD key = release ? keys[keys.size() - 1 - index] : keys[index];
        inputs[index].type = INPUT_KEYBOARD;
        inputs[index].ki.wVk = key;
        inputs[index].ki.dwFlags =
            (release ? KEYEVENTF_KEYUP : 0) |
            (IsExtendedKey(key) ? KEYEVENTF_EXTENDEDKEY : 0);
    }

    return SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT)) == inputs.size();
}

bool SendRightClickAtPoint(const PointD& point)
{
    SetCursorPos(
        static_cast<int>(std::lround(point.x)),
        static_cast<int>(std::lround(point.y)));

    std::array<INPUT, 2> inputs{};
    inputs[0].type = INPUT_MOUSE;
    inputs[0].mi.dwFlags = MOUSEEVENTF_RIGHTDOWN;
    inputs[1].type = INPUT_MOUSE;
    inputs[1].mi.dwFlags = MOUSEEVENTF_RIGHTUP;
    return SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT)) == inputs.size();
}
bool BeginOneFingerKeyHold()
{
    if (g_session.oneFingerKeyHoldActive)
    {
        return true;
    }

    std::vector<WORD> keys;
    {
        std::lock_guard<std::mutex> lock(g_bindingsMutex);
        const auto found = g_bindings.find(Gesture::OneFingerHold);
        if (found != g_bindings.end())
        {
            keys = found->second;
        }
    }

    if (keys.empty())
    {
        return true;
    }

    if (!SendHeldKeyStateInput(keys, false))
    {
        SendHeldKeyStateInput(keys, true);
        return false;
    }

    g_session.oneFingerHeldKeys = keys;
    g_session.oneFingerKeyHoldActive = true;
    return true;
}

bool BeginOneFingerMouseHold(const PointD& point)
{
    if (g_session.oneFingerMouseHoldActive)
    {
        return true;
    }

    SetCursorPos(
        static_cast<int>(std::lround(point.x)),
        static_cast<int>(std::lround(point.y)));
    INPUT input{};
    input.type = INPUT_MOUSE;
    input.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
    if (SendInput(1, &input, sizeof(INPUT)) != 1)
    {
        return false;
    }

    g_session.oneFingerMouseHoldActive = true;
    return true;
}

void UpdateOneFingerMouseHold(const PointD& point)
{
    if (!g_session.oneFingerMouseHoldActive)
    {
        return;
    }

    SetCursorPos(
        static_cast<int>(std::lround(point.x)),
        static_cast<int>(std::lround(point.y)));
}

void EndOneFingerMouseHold()
{
    if (!g_session.oneFingerMouseHoldActive)
    {
        return;
    }

    INPUT input{};
    input.type = INPUT_MOUSE;
    input.mi.dwFlags = MOUSEEVENTF_LEFTUP;
    SendInput(1, &input, sizeof(INPUT));
    g_session.oneFingerMouseHoldActive = false;
}
void EndOneFingerKeyHold()
{
    if (!g_session.oneFingerKeyHoldActive)
    {
        return;
    }

    SendHeldKeyStateInput(g_session.oneFingerHeldKeys, true);
    g_session.oneFingerHeldKeys.clear();
    g_session.oneFingerKeyHoldActive = false;
}
void PostKeyComboToWindow(HWND window, const std::vector<WORD>& keys)
{
    if (window == nullptr || keys.empty())
    {
        return;
    }
    for (const WORD key : keys)
    {
        PostMessageW(window, WM_KEYDOWN, key, 0);
    }
    for (auto it = keys.rbegin(); it != keys.rend(); ++it)
    {
        PostMessageW(window, WM_KEYUP, *it, 0xC0000000);
    }
}

void PostKeyCombo(const std::vector<WORD>& keys)
{
    // Send exactly once. Posting to every child window caused one gesture to
    // become many key presses in applications with several visible controls.
    PostKeyComboToWindow(FindKeyboardTarget(), keys);
}


HWND FindMouseTarget(const PointD& screenPoint)
{
    const POINT point{
        static_cast<LONG>(std::lround(screenPoint.x)),
        static_cast<LONG>(std::lround(screenPoint.y))
    };
    HWND target = WindowFromPoint(point);
    DWORD processId = 0;
    if (target != nullptr)
    {
        GetWindowThreadProcessId(target, &processId);
    }
    return processId == GetCurrentProcessId() ? target : FindKeyboardTarget();
}

LPARAM MakeMouseLParam(HWND target, const PointD& screenPoint)
{
    POINT point{
        static_cast<LONG>(std::lround(screenPoint.x)),
        static_cast<LONG>(std::lround(screenPoint.y))
    };
    ScreenToClient(target, &point);
    return MAKELPARAM(static_cast<WORD>(point.x), static_cast<WORD>(point.y));
}

bool BeginPanDrag(const PointD& screenPoint)
{
    if (g_session.panDragActive)
    {
        return true;
    }

    const HWND keyboardTarget = FindKeyboardTarget();
    const HWND mouseTarget = FindMouseTarget(screenPoint);
    if (keyboardTarget == nullptr || mouseTarget == nullptr)
    {
        return false;
    }

    std::vector<WORD> panKeys;
    {
        std::lock_guard<std::mutex> lock(g_bindingsMutex);
        panKeys = g_panBinding;
    }
    if (panKeys.empty())
    {
        return false;
    }

    g_session.panKeyboardTarget = keyboardTarget;
    g_session.panMouseTarget = mouseTarget;
    g_session.panLastPoint = screenPoint;
    g_session.panKeys = panKeys;

    for (const WORD key : g_session.panKeys)
    {
        PostMessageW(keyboardTarget, WM_KEYDOWN, key, 0);
    }
    const LPARAM position = MakeMouseLParam(mouseTarget, screenPoint);
    PostMessageW(mouseTarget, WM_MOUSEMOVE, 0, position);
    PostMessageW(mouseTarget, WM_LBUTTONDOWN, MK_LBUTTON, position);
    g_session.panDragActive = true;
    return true;
}

void UpdatePanDrag(const PointD& screenPoint)
{
    if (!g_session.panDragActive || g_session.panMouseTarget == nullptr)
    {
        return;
    }

    g_session.panLastPoint = screenPoint;
    PostMessageW(
        g_session.panMouseTarget,
        WM_MOUSEMOVE,
        MK_LBUTTON,
        MakeMouseLParam(g_session.panMouseTarget, screenPoint));
}

void EndPanDrag()
{
    if (!g_session.panDragActive)
    {
        return;
    }

    if (g_session.panMouseTarget != nullptr)
    {
        PostMessageW(
            g_session.panMouseTarget,
            WM_LBUTTONUP,
            0,
            MakeMouseLParam(g_session.panMouseTarget, g_session.panLastPoint));
    }
    if (g_session.panKeyboardTarget != nullptr)
    {
        for (auto key = g_session.panKeys.rbegin(); key != g_session.panKeys.rend(); ++key)
        {
            PostMessageW(g_session.panKeyboardTarget, WM_KEYUP, *key, 0xC0000000);
        }
    }

    g_session.panDragActive = false;
    g_session.panKeyboardTarget = nullptr;
    g_session.panMouseTarget = nullptr;
    g_session.panKeys.clear();
}

std::string Trim(const std::string& value)
{
    size_t start = 0;
    while (start < value.size() && std::isspace(static_cast<unsigned char>(value[start]))) ++start;
    size_t end = value.size();
    while (end > start && std::isspace(static_cast<unsigned char>(value[end - 1]))) --end;
    return value.substr(start, end - start);
}

std::string ToUpper(std::string value)
{
    for (char& ch : value)
    {
        ch = static_cast<char>(std::toupper(static_cast<unsigned char>(ch)));
    }
    return value;
}

bool TryMapKeyToken(const std::string& token, WORD& key)
{
    const std::string upper = ToUpper(Trim(token));
    if (upper.empty()) return false;
    if (upper.size() == 1 && upper[0] >= 'A' && upper[0] <= 'Z') { key = static_cast<WORD>(upper[0]); return true; }
    if (upper.size() == 1 && upper[0] >= '0' && upper[0] <= '9') { key = static_cast<WORD>(upper[0]); return true; }
    if (upper == "CTRL" || upper == "CONTROL") { key = VK_CONTROL; return true; }
    if (upper == "SHIFT") { key = VK_SHIFT; return true; }
    if (upper == "ALT") { key = VK_MENU; return true; }
    if (upper == "WIN" || upper == "WINDOWS") { key = VK_LWIN; return true; }
    if (upper == "PAGEUP" || upper == "PGUP") { key = VK_PRIOR; return true; }
    if (upper == "PAGEDOWN" || upper == "PGDN") { key = VK_NEXT; return true; }
    if (upper == "UP") { key = VK_UP; return true; }
    if (upper == "DOWN") { key = VK_DOWN; return true; }
    if (upper == "LEFT") { key = VK_LEFT; return true; }
    if (upper == "RIGHT") { key = VK_RIGHT; return true; }
    if (upper == "HOME") { key = VK_HOME; return true; }
    if (upper == "END") { key = VK_END; return true; }
    if (upper == "INSERT" || upper == "INS") { key = VK_INSERT; return true; }
    if (upper == "DELETE" || upper == "DEL") { key = VK_DELETE; return true; }
    if (upper == "SPACE") { key = VK_SPACE; return true; }
    if (upper == "PLUS") { key = VK_OEM_PLUS; return true; }
    if (upper == "MINUS") { key = VK_OEM_MINUS; return true; }
    if (upper.size() >= 2 && upper[0] == 'F')
    {
        const int number = std::atoi(upper.c_str() + 1);
        if (number >= 1 && number <= 24)
        {
            key = static_cast<WORD>(VK_F1 + number - 1);
            return true;
        }
    }
    return false;
}

bool TryParseShortcut(const std::string& shortcut, std::vector<WORD>& keys)
{
    keys.clear();
    std::stringstream stream(shortcut);
    std::string token;
    while (std::getline(stream, token, '+'))
    {
        WORD key = 0;
        if (!TryMapKeyToken(token, key))
        {
            keys.clear();
            return false;
        }
        keys.push_back(key);
    }
    return !keys.empty() && keys.size() <= 8;
}

bool TryParseGesture(const std::string& name, Gesture& gesture)
{
    if (name == "pinch-open") gesture = Gesture::PinchOpen;
    else if (name == "pinch-close") gesture = Gesture::PinchClose;
    else if (name == "rotate-cw") gesture = Gesture::RotateClockwise;
    else if (name == "rotate-ccw") gesture = Gesture::RotateCounterClockwise;
    else if (name == "one-finger-slide") gesture = Gesture::OneFingerSlide;
    else if (name == "one-finger-hold") gesture = Gesture::OneFingerHold;

    else if (name == "undo") gesture = Gesture::TwoFingerTap;
    else if (name == "redo" || name == "redo-three") gesture = Gesture::ThreeFingerTap;
    else if (name == "redo-four") gesture = Gesture::FourFingerTap;
    else return false;
    return true;
}

void SetDefaultBindings()
{
    std::lock_guard<std::mutex> lock(g_bindingsMutex);
    g_bindings = {
        { Gesture::PinchOpen, { VK_PRIOR } },
        { Gesture::PinchClose, { VK_NEXT } },
        { Gesture::RotateClockwise, { VK_SHIFT, VK_NEXT } },
        { Gesture::RotateCounterClockwise, { VK_SHIFT, VK_PRIOR } },
        { Gesture::OneFingerSlide, {} },
        { Gesture::OneFingerHold, {} },

        { Gesture::TwoFingerTap, { VK_CONTROL, 'Z' } },
        { Gesture::ThreeFingerTap, { VK_CONTROL, 'Y' } },
        { Gesture::FourFingerTap, { VK_CONTROL, 'Y' } }
    };
    g_panBinding = { VK_SPACE };
    g_oneFingerSlideUsesPan = false;
    g_oneFingerHoldUsesRightClick = false;
}

void ExecuteGesture(Gesture gesture)
{
    std::vector<WORD> keys;
    {
        std::lock_guard<std::mutex> lock(g_bindingsMutex);
        const auto found = g_bindings.find(gesture);
        if (found == g_bindings.end() || found->second.empty())
        {
            return;
        }
        keys = found->second;
    }

    QueueGestureDispatch(gesture);
}

bool TryExecuteBindCommand(const std::string& command)
{
    constexpr char prefix[] = "bind ";
    if (command.rfind(prefix, 0) != 0)
    {
        return false;
    }

    const std::string rest = Trim(command.substr(sizeof(prefix) - 1));
    const size_t separator = rest.find(' ');
    if (separator == std::string::npos)
    {
        return false;
    }

    const std::string name = rest.substr(0, separator);
    const std::string shortcut = Trim(rest.substr(separator + 1));
    std::vector<WORD> keys;
    if (!shortcut.empty() && ToUpper(shortcut) != "DISABLED" && !TryParseShortcut(shortcut, keys))
    {
        return false;
    }

    std::lock_guard<std::mutex> lock(g_bindingsMutex);
    if (name == "pan")
    {
        g_panBinding = keys;
        return true;
    }

    Gesture gesture{};
    if (!TryParseGesture(name, gesture))
    {
        return false;
    }

    g_bindings[gesture] = keys;
    return true;
}

bool TryExecuteModeCommand(const std::string& command)
{
    constexpr char slidePrefix[] = "mode one-finger-slide-pan ";
    constexpr char holdPrefix[] = "mode one-finger-hold-right-click ";

    bool* setting = nullptr;
    std::string value;
    if (command.rfind(slidePrefix, 0) == 0)
    {
        setting = &g_oneFingerSlideUsesPan;
        value = ToUpper(Trim(command.substr(sizeof(slidePrefix) - 1)));
    }
    else if (command.rfind(holdPrefix, 0) == 0)
    {
        setting = &g_oneFingerHoldUsesRightClick;
        value = ToUpper(Trim(command.substr(sizeof(holdPrefix) - 1)));
    }
    else
    {
        return false;
    }

    if (value != "ON" && value != "OFF")
    {
        return false;
    }

    std::lock_guard<std::mutex> lock(g_bindingsMutex);
    *setting = value == "ON";
    return true;
}
bool TryExecuteStepCommand(const std::string& command)
{
    constexpr char prefix[] = "step ";
    if (command.rfind(prefix, 0) != 0)
    {
        return false;
    }

    const std::string rest = Trim(command.substr(sizeof(prefix) - 1));
    const size_t separator = rest.find(' ');
    if (separator == std::string::npos)
    {
        return false;
    }

    double value = 0;
    try
    {
        value = std::stod(Trim(rest.substr(separator + 1)));
    }
    catch (...)
    {
        return false;
    }

    std::lock_guard<std::mutex> lock(g_bindingsMutex);
    const std::string kind = rest.substr(0, separator);
    if (kind == "zoom") g_zoomStepPixels = std::clamp(value, 1.0, 60.0);
    else if (kind == "rotate") g_rotateStepDegrees = std::clamp(value, 1.0, 15.0);
    else if (kind == "pan") g_panStepPixels = std::clamp(value, 1.0, 60.0);
    else return false;
    return true;
}

bool TryExecuteIntervalCommand(const std::string& command)
{
    constexpr char prefix[] = "interval ";
    if (command.rfind(prefix, 0) != 0)
    {
        return false;
    }

    const std::string rest = Trim(command.substr(sizeof(prefix) - 1));
    const size_t separator = rest.find(' ');
    if (separator == std::string::npos)
    {
        return false;
    }

    double value = 0;
    try
    {
        value = std::stod(Trim(rest.substr(separator + 1)));
    }
    catch (...)
    {
        return false;
    }

    const DWORD interval = static_cast<DWORD>(std::lround(std::clamp(value, 0.0, 100.0)));
    std::lock_guard<std::mutex> lock(g_bindingsMutex);
    const std::string kind = rest.substr(0, separator);
    if (kind == "zoom") g_zoomIntervalMilliseconds = interval;
    else if (kind == "rotate") g_rotateIntervalMilliseconds = interval;
    else return false;
    return true;
}

bool TryExecuteKeyPostCommand(const std::string& command)
{
    constexpr char prefix[] = "key-post ";
    if (command.rfind(prefix, 0) != 0)
    {
        return false;
    }
    std::vector<WORD> keys;
    if (!TryParseShortcut(command.substr(sizeof(prefix) - 1), keys))
    {
        return false;
    }
    PostKeyCombo(keys);
    return true;
}

bool ExecuteCommand(const std::string& command)
{
    Gesture gesture{};
    std::string normalized = command;
    constexpr char postSuffix[] = "-post";
    if (normalized.size() > sizeof(postSuffix) - 1 &&
        normalized.compare(normalized.size() - (sizeof(postSuffix) - 1), sizeof(postSuffix) - 1, postSuffix) == 0)
    {
        normalized.erase(normalized.size() - (sizeof(postSuffix) - 1));
    }

    if (TryParseGesture(normalized, gesture))
    {
        ExecuteGesture(gesture);
        return true;
    }

    if (command == "pinch-open") SendKeyCombo({ VK_PRIOR });
    else if (command == "pinch-close") SendKeyCombo({ VK_NEXT });
    else if (command == "rotate-cw") SendKeyCombo({ VK_SHIFT, VK_NEXT });
    else if (command == "rotate-ccw") SendKeyCombo({ VK_SHIFT, VK_PRIOR });

    else if (command == "undo") SendKeyCombo({ VK_CONTROL, 'Z' });
    else if (command == "redo") SendKeyCombo({ VK_CONTROL, 'Y' });
    else return false;
    return true;
}

double Distance(const PointD& first, const PointD& second)
{
    const double deltaX = first.x - second.x;
    const double deltaY = first.y - second.y;
    return std::sqrt(deltaX * deltaX + deltaY * deltaY);
}

PointD Centroid(const std::map<UINT32, PointD>& contacts)
{
    PointD result{};
    for (const auto& contact : contacts)
    {
        result.x += contact.second.x;
        result.y += contact.second.y;
    }
    result.x /= static_cast<double>(contacts.size());
    result.y /= static_cast<double>(contacts.size());
    return result;
}

double AngleDegrees(const PointD& first, const PointD& second)
{
    return std::atan2(second.y - first.y, second.x - first.x) * 180.0 / 3.14159265358979323846;
}

double NormalizeAngle(double angle)
{
    while (angle > 180.0) angle -= 360.0;
    while (angle < -180.0) angle += 360.0;
    return angle;
}

void StartSession()
{
    CancelOneFingerHoldTimer();
    EndOneFingerMouseHold();
    EndOneFingerKeyHold();
    EndPanDrag();
    g_session = {};
    g_session.active = true;
    g_session.started = std::chrono::steady_clock::now();
    g_debugSessionEventCount.store(0, std::memory_order_relaxed);
}

void TrackContact(UINT32 id, const PointD& point)
{
    const auto found = g_session.firstPositions.find(id);
    if (found == g_session.firstPositions.end())
    {
        g_session.firstPositions[id] = point;
    }
    else
    {
        g_session.maxMovement = std::max(g_session.maxMovement, Distance(found->second, point));
    }
    g_session.peakContacts = std::max(g_session.peakContacts, static_cast<int>(g_contacts.size()));
}

void ResetPairBaseline()
{
    if (g_contacts.size() != 2)
    {
        EndPanDrag();
        g_session.pairValid = false;
        return;
    }

    auto first = g_contacts.begin();
    auto second = std::next(first);
    g_session.pairFirst = first->first;
    g_session.pairSecond = second->first;
    g_session.pairCentroid = Centroid(g_contacts);
    g_session.pairDistance = Distance(first->second, second->second);
    g_session.pairAngle = AngleDegrees(first->second, second->second);
    g_session.panAccumulatorX = 0;
    g_session.panAccumulatorY = 0;
    g_session.zoomAccumulator = 0;
    g_session.rotateAccumulator = 0;
    g_session.pairValid = true;
}

void GetResponseSettings(double& zoomStep, double& rotateStep, double& panStep)
{
    std::lock_guard<std::mutex> lock(g_bindingsMutex);
    zoomStep = g_zoomStepPixels;
    rotateStep = g_rotateStepDegrees;
    panStep = g_panStepPixels;
}

bool SendGestureInput(Gesture gesture)
{
    std::vector<WORD> keys;
    {
        std::lock_guard<std::mutex> lock(g_bindingsMutex);
        const auto found = g_bindings.find(gesture);
        if (found == g_bindings.end() || found->second.empty())
        {
            return false;
        }
        keys = found->second;
    }

    return SendKeyComboInput(keys);
}

DWORD WINAPI GestureDispatchThread(LPVOID)
{
    while (true)
    {
        WaitForSingleObject(g_gestureDispatchEvent, INFINITE);
        while (true)
        {
            Gesture gesture{};
            {
                std::lock_guard<std::mutex> lock(g_gestureQueueMutex);
                if (g_gestureQueue.empty())
                {
                    break;
                }
                gesture = g_gestureQueue.front();
                g_gestureQueue.pop_front();
            }
            if (SendGestureInput(gesture))
            {
                RecordDebugGesture(static_cast<int>(gesture));
            }
        }
        if (g_gestureDispatchStopping.load())
        {
            break;
        }
    }
    return 0;
}

bool StartGestureDispatcher()
{
    g_gestureDispatchStopping.store(false);
    g_gestureDispatchEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (g_gestureDispatchEvent == nullptr)
    {
        return false;
    }
    g_gestureDispatchThread = CreateThread(nullptr, 0, GestureDispatchThread, nullptr, 0, nullptr);
    if (g_gestureDispatchThread == nullptr)
    {
        CloseHandle(g_gestureDispatchEvent);
        g_gestureDispatchEvent = nullptr;
        return false;
    }
    return true;
}

void StopGestureDispatcher()
{
    if (g_gestureDispatchEvent == nullptr)
    {
        return;
    }
    g_gestureDispatchStopping.store(true);
    SetEvent(g_gestureDispatchEvent);
    if (g_gestureDispatchThread != nullptr)
    {
        WaitForSingleObject(g_gestureDispatchThread, 1000);
        CloseHandle(g_gestureDispatchThread);
        g_gestureDispatchThread = nullptr;
    }
    CloseHandle(g_gestureDispatchEvent);
    g_gestureDispatchEvent = nullptr;
    std::lock_guard<std::mutex> lock(g_gestureQueueMutex);
    g_gestureQueue.clear();
}

bool TryReserveTransformDispatch(Gesture gesture)
{
    DWORD interval = 0;
    std::atomic<ULONGLONG>* lastDispatch = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_bindingsMutex);
        if (gesture == Gesture::PinchOpen || gesture == Gesture::PinchClose)
        {
            interval = g_zoomIntervalMilliseconds;
            lastDispatch = &g_lastZoomDispatchTick;
        }
        else if (gesture == Gesture::RotateClockwise || gesture == Gesture::RotateCounterClockwise)
        {
            interval = g_rotateIntervalMilliseconds;
            lastDispatch = &g_lastRotateDispatchTick;
        }
    }

    if (lastDispatch == nullptr || interval == 0)
    {
        return true;
    }

    const ULONGLONG now = GetTickCount64();
    const ULONGLONG previous = lastDispatch->load(std::memory_order_relaxed);
    if (previous != 0 && now - previous < interval)
    {
        return false;
    }
    lastDispatch->store(now, std::memory_order_relaxed);
    return true;
}

bool FireGesture(Gesture gesture, const PointD* zoomAnchor = nullptr)
{
    if (!TryReserveTransformDispatch(gesture))
    {
        return false;
    }

    if (zoomAnchor != nullptr &&
        (gesture == Gesture::PinchOpen || gesture == Gesture::PinchClose))
    {
        SetCursorPos(
            static_cast<int>(std::lround(zoomAnchor->x)),
            static_cast<int>(std::lround(zoomAnchor->y)));
    }

    g_session.fired = true;
    return QueueGestureDispatch(gesture);
}

void EvaluateManipulation()
{
    if (g_contacts.size() != 2)
    {
        EndPanDrag();
        g_session.pairValid = false;
        return;
    }

    auto first = g_contacts.begin();
    auto second = std::next(first);
    if (!g_session.pairValid ||
        g_session.pairFirst != first->first ||
        g_session.pairSecond != second->first)
    {
        ResetPairBaseline();
        UpdateDebugSnapshot();
        return;
    }

    double zoomStep = 0;
    double rotateStep = 0;
    double panStep = 0;
    GetResponseSettings(zoomStep, rotateStep, panStep);
    const PointD centroid = Centroid(g_contacts);
    const double distance = Distance(first->second, second->second);
    const double angle = AngleDegrees(first->second, second->second);
    g_session.panAccumulatorX = centroid.x - g_session.pairCentroid.x;
    g_session.panAccumulatorY = centroid.y - g_session.pairCentroid.y;
    g_session.zoomAccumulator = distance - g_session.pairDistance;
    g_session.rotateAccumulator = NormalizeAngle(angle - g_session.pairAngle);

    const double zoomStrength = std::abs(g_session.zoomAccumulator) / zoomStep;
    const double rotateStrength = std::abs(g_session.rotateAccumulator) / rotateStep;
    bool completedStep = false;
    if (zoomStrength >= 1 || rotateStrength >= 1)
    {
        if (zoomStrength >= rotateStrength)
        {
            completedStep = FireGesture(
                g_session.zoomAccumulator > 0 ? Gesture::PinchOpen : Gesture::PinchClose,
                &centroid);
        }
        else
        {
            completedStep = FireGesture(g_session.rotateAccumulator > 0
                ? Gesture::RotateClockwise
                : Gesture::RotateCounterClockwise);
        }
    }

    if (!g_session.panDragActive &&
        (std::abs(g_session.panAccumulatorX) >= panStep ||
         std::abs(g_session.panAccumulatorY) >= panStep))
    {
        if (BeginPanDrag(centroid))
        {
            RecordDebugGesture(PanDebugGesture);
            g_session.fired = true;
            completedStep = true;
        }
    }
    UpdatePanDrag(centroid);
    UpdateDebugSnapshot();
    if (completedStep || g_session.panDragActive)
    {
        ResetPairBaseline();
    }
}

void FinishSession()
{
    CancelOneFingerHoldTimer();
    EndOneFingerMouseHold();
    EndOneFingerKeyHold();
    EndPanDrag();
    if (!g_session.active)
    {
        return;
    }
    const auto duration = std::chrono::steady_clock::now() - g_session.started;
    if (!g_session.fired &&
        duration <= TapDuration &&
        g_session.maxMovement <= TapMovementPixels)
    {
        if (g_session.peakContacts >= 4)
        {
            ExecuteGesture(Gesture::FourFingerTap);
        }
        else if (g_session.peakContacts == 3)
        {
            ExecuteGesture(Gesture::ThreeFingerTap);
        }
        else if (g_session.peakContacts == 2)
        {
            ExecuteGesture(Gesture::TwoFingerTap);
        }
    }

    g_session = {};
}

void ProcessPointerMessage(UINT message, WPARAM wParam)
{
    const UINT32 pointerId = GET_POINTERID_WPARAM(wParam);
    POINTER_INPUT_TYPE pointerType{};
    if (!GetPointerType(pointerId, &pointerType))
    {
        g_debugPointerInfoFailureCount.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (pointerType != PT_TOUCH)
    {
        return;
    }
    g_debugTouchMessageCount.fetch_add(1, std::memory_order_relaxed);

    POINTER_TOUCH_INFO touchInfo{};
    if (!GetPointerTouchInfo(pointerId, &touchInfo))
    {
        g_debugPointerInfoFailureCount.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    const PointD point{
        static_cast<double>(touchInfo.pointerInfo.ptPixelLocation.x),
        static_cast<double>(touchInfo.pointerInfo.ptPixelLocation.y)
    };

    if (message == WM_POINTERDOWN_VALUE)
    {
        if (g_contacts.empty())
        {
            StartSession();
        }
        g_contacts[pointerId] = point;
        TrackContact(pointerId, point);
        CancelOneFingerHoldTimer();
        if (g_contacts.size() == 1)
        {
            g_oneFingerHoldTimer = SetTimer(nullptr, 0, OneFingerHoldMilliseconds, nullptr);
        }
        else
        {
            EndOneFingerKeyHold();
        }
        if (g_contacts.size() != 2)
        {
            EndPanDrag();
        }
        ResetPairBaseline();
        UpdateDebugSnapshot();
    }
    else if (message == WM_POINTERUPDATE_VALUE)
    {
        if ((touchInfo.pointerInfo.pointerFlags & POINTER_FLAG_INCONTACT) == 0)
        {
            return;
        }

        g_contacts[pointerId] = point;
        TrackContact(pointerId, point);
        if (g_contacts.size() == 1 && g_session.peakContacts == 1)
        {
            bool usePan = false;
            double slideThreshold = OneFingerSlidePixels;
            {
                std::lock_guard<std::mutex> lock(g_bindingsMutex);
                usePan = g_oneFingerSlideUsesPan;
                if (usePan)
                {
                    slideThreshold = g_panStepPixels;
                }
            }

            if (!g_session.fired)
            {
                if (g_session.maxMovement > OneFingerHoldMovementPixels)
                {
                    CancelOneFingerHoldTimer();
                }
                if (g_session.maxMovement >= slideThreshold)
                {
                    CancelOneFingerHoldTimer();
                    if (usePan)
                    {
                        if (BeginPanDrag(point))
                        {
                            g_session.fired = true;
                            RecordDebugGesture(PanDebugGesture);
                        }
                    }
                    else
                    {
                        FireGesture(Gesture::OneFingerSlide);
                    }
                }
            }
            UpdateOneFingerMouseHold(point);
            UpdatePanDrag(point);
        }
        else
        {
            EvaluateManipulation();
        }
        g_debugRawUpdateCount.fetch_add(1, std::memory_order_relaxed);
        g_debugEvaluatedFrameCount.fetch_add(1, std::memory_order_relaxed);
        UpdateDebugSnapshot();
    }
    else if (message == WM_POINTERUP_VALUE)
    {
        CancelOneFingerHoldTimer();
        EndOneFingerMouseHold();
        EndOneFingerKeyHold();
        g_contacts[pointerId] = point;
        TrackContact(pointerId, point);
        g_contacts.erase(pointerId);
        if (g_contacts.size() != 2)
        {
            EndPanDrag();
        }
        if (g_contacts.empty())
        {
            FinishSession();
            g_debugContacts.store(0, std::memory_order_relaxed);
            g_debugPanActive.store(false, std::memory_order_relaxed);
        }
        else
        {
            ResetPairBaseline();
            UpdateDebugSnapshot();
        }
    }
}

LRESULT CALLBACK PointerMessageHook(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0 && wParam == PM_REMOVE)
    {
        const auto message = reinterpret_cast<MSG*>(lParam);
        if (message->message == WM_POINTERDOWN_VALUE ||
            message->message == WM_POINTERUPDATE_VALUE ||
            message->message == WM_POINTERUP_VALUE)
        {
            g_debugPointerMessageCount.fetch_add(1, std::memory_order_relaxed);
            ProcessPointerMessage(message->message, message->wParam);
        }
        else if (message->message == WM_TIMER &&
                 g_oneFingerHoldTimer != 0 &&
                 message->wParam == g_oneFingerHoldTimer)
        {
            CancelOneFingerHoldTimer();
            if (g_session.active &&
                !g_session.fired &&
                g_contacts.size() == 1 &&
                g_session.peakContacts == 1 &&
                g_session.maxMovement <= OneFingerHoldMovementPixels)
            {
                bool useRightClick = false;
                {
                    std::lock_guard<std::mutex> lock(g_bindingsMutex);
                    useRightClick = g_oneFingerHoldUsesRightClick;
                }

                bool sent = false;
                if (useRightClick)
                {
                    sent = SendRightClickAtPoint(g_contacts.begin()->second);
                }
                else if (BeginOneFingerKeyHold())
                {
                    sent = BeginOneFingerMouseHold(g_contacts.begin()->second);
                    if (!sent)
                    {
                        EndOneFingerKeyHold();
                    }
                }
                if (sent)
                {
                    g_session.fired = true;
                    RecordDebugGesture(static_cast<int>(Gesture::OneFingerHold));
                }
            }
        }

    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

void AddWindowThread(HWND window, std::vector<DWORD>& threadIds)
{
    DWORD processId = 0;
    const DWORD threadId = GetWindowThreadProcessId(window, &processId);
    if (processId != GetCurrentProcessId() || threadId == 0)
    {
        return;
    }

    if (std::find(threadIds.begin(), threadIds.end(), threadId) == threadIds.end())
    {
        threadIds.push_back(threadId);
    }
}

BOOL CALLBACK CollectChildWindowThreadProc(HWND window, LPARAM parameter)
{
    AddWindowThread(window, *reinterpret_cast<std::vector<DWORD>*>(parameter));
    return TRUE;
}

BOOL CALLBACK CollectProcessWindowThreadsProc(HWND window, LPARAM parameter)
{
    auto& threadIds = *reinterpret_cast<std::vector<DWORD>*>(parameter);
    DWORD processId = 0;
    GetWindowThreadProcessId(window, &processId);
    if (processId == GetCurrentProcessId())
    {
        AddWindowThread(window, threadIds);
        EnumChildWindows(window, CollectChildWindowThreadProc, parameter);
    }
    return TRUE;
}

bool InstallPointerHookOnce()
{
    std::vector<DWORD> threadIds;
    EnumWindows(CollectProcessWindowThreadsProc, reinterpret_cast<LPARAM>(&threadIds));
    if (threadIds.empty())
    {
        return false;
    }

    for (const DWORD threadId : threadIds)
    {
        HHOOK hook = SetWindowsHookExW(WH_GETMESSAGE, PointerMessageHook, g_module, threadId);
        if (hook != nullptr)
        {
            g_pointerHooks.push_back(hook);
        }
    }
    return !g_pointerHooks.empty();
}
bool InstallPointerHookWithRetry(DWORD timeoutMilliseconds)
{
    const ULONGLONG deadline = GetTickCount64() + timeoutMilliseconds;
    do
    {
        if (InstallPointerHookOnce())
        {
            return true;
        }
        Sleep(200);
    }
    while (GetTickCount64() < deadline);

    return false;
}

void RemovePointerHook()
{
    CancelOneFingerHoldTimer();
    EndOneFingerMouseHold();
    EndOneFingerKeyHold();
    EndPanDrag();
    for (const HHOOK hook : g_pointerHooks)
    {
        if (hook != nullptr)
        {
            UnhookWindowsHookEx(hook);
        }
    }
    g_pointerHooks.clear();
}

DWORD WINAPI BridgeThread(LPVOID)
{
    SetDefaultBindings();
    const bool dispatcherStarted = StartGestureDispatcher();
    const bool hookInstalled = InstallPointerHookWithRetry(15000);
    g_debugHookInstalled.store(hookInstalled, std::memory_order_relaxed);

    const std::wstring pipeName = GetPipeName();
    HANDLE pipe = CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
    {
        RemovePointerHook();
        StopGestureDispatcher();
        return 2;
    }

    WriteLine(pipe, hookInstalled && dispatcherStarted
        ? "{\"state\":\"ok\",\"detail\":\"native-pointer-hook-installed\"}"
        : "{\"state\":\"error\",\"detail\":\"pointer-hook-or-dispatcher-failed\"}");

    std::string command;
    while (ReadLine(pipe, command))
    {
        if (command == "quit")
        {
            WriteLine(pipe, "{\"state\":\"ok\",\"detail\":\"quit\"}");
            break;
        }
        if (command == "debug")
        {
            const std::string snapshot = BuildDebugSnapshot();
            WriteLine(pipe, snapshot.c_str());
            continue;
        }

        const bool sent =
            TryExecuteBindCommand(command) ||
            TryExecuteModeCommand(command) ||
            TryExecuteStepCommand(command) ||
            TryExecuteIntervalCommand(command) ||
            TryExecuteKeyPostCommand(command) ||
            ExecuteCommand(command);
        WriteLine(pipe, sent
            ? "{\"state\":\"ok\",\"detail\":\"command-sent\"}"
            : "{\"state\":\"error\",\"detail\":\"unknown-command\"}");
    }

    RemovePointerHook();
    StopGestureDispatcher();
    CloseHandle(pipe);
    return 0;
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, BridgeThread, nullptr, 0, nullptr);
        if (thread != nullptr)
        {
            CloseHandle(thread);
        }
    }
    return TRUE;
}