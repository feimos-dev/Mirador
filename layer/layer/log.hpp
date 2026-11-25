// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <sal.h>

#include <memory>
#include <optional>
#include <string>
#include <string_view>

namespace dvl {

enum class Level { Off, Error, Warn, Info, Debug };

std::optional<Level> parse_level(std::string_view text);
const char* level_name(Level level);

struct SessionLine {
    Level level;
    std::string_view text;
};
SessionLine session_line(std::string_view line);

std::optional<std::wstring> trace_folder(const std::optional<std::string>& setting, const std::wstring& log_dir);

inline constexpr int kLogsKept = 5;

void rotate_logs(const std::wstring& dir, int keep = kLogsKept);

struct LoggerConfig {
    std::wstring dir;
    Level threshold = Level::Info;
    bool to_stderr = true;
    bool to_debugger = true;
    int keep = kLogsKept;
};

class Logger {
public:
    explicit Logger(LoggerConfig config);
    ~Logger();
    Logger(const Logger&) = delete;
    Logger& operator=(const Logger&) = delete;

    void write(Level level, std::string_view message, bool rotate = true);

    Level threshold() const;
    const std::wstring& dir() const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

Logger& process_logger();

void log(Level level, _Printf_format_string_ const char* format, ...);
void log_append(Level level, _Printf_format_string_ const char* format, ...);

using LogSink = void (*)(Level level, const char* line, void* context);
void set_log_sink_for_tests(LogSink sink, void* context);

}
