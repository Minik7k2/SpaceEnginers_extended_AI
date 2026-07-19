#include "bridge.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <sstream>

namespace zf {

namespace {

namespace fs = std::filesystem;

std::string rotated_filename(const std::string& prefix, int index) {
    if (index <= 1) {
        return prefix + ".jsonl";
    }
    std::array<char, 32> suffix{};
    std::snprintf(suffix.data(), suffix.size(), "-%04d.jsonl", index);
    return prefix + suffix.data();
}

std::int64_t now_unix_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

// Zwraca kompletne (zakończone '\n') linie pliku, w kolejności. Ewentualna niedopisana
// końcówka (torn write) jest celowo pomijana — spec: "niepełną linię pomiń i czekaj".
std::vector<std::string> read_complete_lines(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        return {};
    }
    std::ostringstream buf;
    buf << in.rdbuf();
    const std::string content = buf.str();

    std::vector<std::string> lines;
    std::size_t start = 0;
    while (true) {
        const std::size_t nl = content.find('\n', start);
        if (nl == std::string::npos) {
            break;
        }
        lines.push_back(content.substr(start, nl - start));
        start = nl + 1;
    }
    return lines;
}

} // namespace

EventReader::EventReader(std::string storage_dir, Db& db)
    : storage_dir_(std::move(storage_dir)), db_(db) {}

std::vector<Event> EventReader::poll() {
    std::vector<Event> events;

    const fs::path dir(storage_dir_);
    int active_index = 0;
    for (int idx = 1;; ++idx) {
        if (!fs::exists(dir / rotated_filename("events", idx))) {
            break;
        }
        active_index = idx;
    }
    if (active_index == 0) {
        return events; // mod jeszcze nic nie napisał do storage
    }

    for (int idx = 1; idx <= active_index; ++idx) {
        const std::string filename = rotated_filename("events", idx);
        const fs::path path = dir / filename;

        const std::vector<std::string> lines = read_complete_lines(path);
        const std::int64_t offset = std::max<std::int64_t>(db_.get_line_offset(filename), 0);

        for (std::size_t i = static_cast<std::size_t>(offset); i < lines.size(); ++i) {
            const std::string& line = lines[i];
            if (line.empty()) {
                continue;
            }
            try {
                const nlohmann::json parsed = nlohmann::json::parse(line);
                const int v = parsed.value("v", 1);
                if (v > 1) {
                    continue; // nieobsługiwana nowsza wersja formatu — pomiń, licz jako przetworzoną
                }
                Event ev;
                ev.type = parsed.value("type", std::string{});
                ev.data = parsed.value("data", nlohmann::json::object());
                if (!ev.type.empty()) {
                    events.push_back(std::move(ev));
                }
            } catch (const nlohmann::json::exception& e) {
                std::cerr << "[brain] uszkodzona linia w " << filename << " (#" << i << "): " << e.what() << "\n";
            }
        }

        if (static_cast<std::size_t>(offset) < lines.size()) {
            db_.set_line_offset(filename, static_cast<std::int64_t>(lines.size()));
        }

        if (idx < active_index && lines.size() == static_cast<std::size_t>(db_.get_line_offset(filename))) {
            std::error_code ec;
            fs::remove(path, ec); // plik już rotowany i w pełni przetworzony — bezpiecznie skasować
        }
    }

    return events;
}

CommandWriter::CommandWriter(std::string storage_dir, Db& db, std::uint64_t rotate_bytes)
    : storage_dir_(std::move(storage_dir)), db_(db), rotate_bytes_(rotate_bytes) {
    fs::create_directories(storage_dir_);

    for (int idx = 1;; ++idx) {
        if (!fs::exists(fs::path(storage_dir_) / rotated_filename("commands", idx))) {
            break;
        }
        current_index_ = idx;
    }
    std::error_code ec;
    const fs::path path = active_path();
    current_bytes_ = fs::exists(path) ? fs::file_size(path, ec) : 0;
}

std::string CommandWriter::active_path() const {
    return (fs::path(storage_dir_) / rotated_filename("commands", current_index_)).string();
}

void CommandWriter::rotate_if_needed() {
    if (current_bytes_ < rotate_bytes_) {
        return;
    }
    ++current_index_;
    current_bytes_ = 0;
}

void CommandWriter::write_line(const nlohmann::json& line) {
    const std::string text = line.dump() + "\n";
    std::ofstream out(active_path(), std::ios::app | std::ios::binary);
    if (!out) {
        std::cerr << "[brain] nie można dopisać do " << active_path() << " — komenda pominięta\n";
        return;
    }
    out << text;
    out.close();
    current_bytes_ += text.size();
    rotate_if_needed();
}

void CommandWriter::write_radio_message(const std::string& faction, const std::string& text,
                                         const std::string& color, int priority) {
    const nlohmann::json line = {
        {"v", 1},
        {"seq", db_.next_commands_seq()},
        {"ts", now_unix_ms()},
        {"type", "radio_message"},
        {"data", {
            {"faction", faction},
            {"text", text},
            {"color", color},
            {"priority", priority},
        }},
    };
    write_line(line);
}

} // namespace zf
