// Build in an x64 Visual Studio Native Tools prompt: cl /nologo /W4 /EHsc VerifyEtwLayouts.cpp
// This is a build-time SDK check, never called by the application.
#include <windows.h>
#include <evntrace.h>
#include <evntcons.h>
#include <tdh.h>
#include <stddef.h>
static_assert(sizeof(void*) == 8, "Use x64 Native Tools");
static_assert(sizeof(WNODE_HEADER) == 48, "WNODE_HEADER");
static_assert(sizeof(EVENT_TRACE_PROPERTIES) == 120, "EVENT_TRACE_PROPERTIES");
static_assert(sizeof(CLASSIC_EVENT_ID) == 24, "CLASSIC_EVENT_ID");
static_assert(sizeof(EVENT_HEADER) == 80, "EVENT_HEADER");
static_assert(sizeof(EVENT_RECORD) == 112, "EVENT_RECORD");
static_assert(sizeof(EVENT_TRACE_HEADER) == 48, "EVENT_TRACE_HEADER");
static_assert(sizeof(EVENT_TRACE) == 88, "EVENT_TRACE");
static_assert(sizeof(TIME_ZONE_INFORMATION) == 172, "TIME_ZONE_INFORMATION");
static_assert(sizeof(TRACE_LOGFILE_HEADER) == 280, "TRACE_LOGFILE_HEADER");
static_assert(sizeof(EVENT_TRACE_LOGFILEW) == 448, "EVENT_TRACE_LOGFILEW");
static_assert(sizeof(PROPERTY_DATA_DESCRIPTOR) == 16, "PROPERTY_DATA_DESCRIPTOR");
static_assert(offsetof(EVENT_RECORD, UserData) == 96, "UserData");
static_assert(offsetof(EVENT_TRACE_LOGFILEW, ProcessTraceMode) == 28, "ProcessTraceMode");
static_assert(offsetof(EVENT_TRACE_LOGFILEW, LogfileHeader) == 120, "LogfileHeader");
static_assert(offsetof(EVENT_TRACE_LOGFILEW, EventRecordCallback) == 424, "EventRecordCallback");
static_assert(TraceStackTracingInfo == 3 && TraceSystemTraceEnableFlagsInfo == 4, "TRACE_INFO_CLASS");
int main() { return 0; }
