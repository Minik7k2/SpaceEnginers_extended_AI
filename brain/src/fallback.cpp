#include "fallback.hpp"

#include <filesystem>
#include <iostream>

#include <toml++/toml.hpp>

namespace zf {

namespace {

std::int64_t file_mtime(const std::string& path) {
    std::error_code ec;
    const auto t = std::filesystem::last_write_time(path, ec);
    return ec ? 0 : static_cast<std::int64_t>(t.time_since_epoch().count());
}

} // namespace

Fallback::Fallback(std::string path) : path_(std::move(path)) {
    poll();
}

bool Fallback::poll() {
    const std::int64_t m = file_mtime(path_);
    if (m == mtime_) {
        return false;
    }
    mtime_ = m;
    load();
    return true;
}

void Fallback::load() {
    templates_.clear();
    toml::table tbl;
    try {
        tbl = toml::parse_file(path_);
    } catch (const toml::parse_error& err) {
        std::cerr << "[brain] nie można sparsować " << path_ << ": " << err.description()
                  << " — radio fallback wyłączone do naprawy pliku\n";
        return;
    }

    for (const auto& [faction_key, faction_node] : tbl) {
        const auto* section = faction_node.as_table();
        if (section == nullptr) {
            continue;
        }
        auto& kinds = templates_[std::string(faction_key.str())];
        for (const auto& [kind_key, kind_node] : *section) {
            if (const auto text = kind_node.value<std::string>()) {
                kinds[std::string(kind_key.str())] = *text;
            }
        }
    }
    std::cout << "[brain] szablony fallback: " << templates_.size() << " frakcji z " << path_ << "\n";
}

bool Fallback::has(const std::string& faction, const std::string& kind) const {
    const auto it = templates_.find(faction);
    return it != templates_.end() && it->second.count(kind) > 0;
}

std::string Fallback::render(const std::string& faction, const std::string& kind,
                             const std::map<std::string, std::string>& vars) const {
    const auto it = templates_.find(faction);
    if (it == templates_.end()) {
        return {};
    }
    const auto kind_it = it->second.find(kind);
    if (kind_it == it->second.end()) {
        return {};
    }

    std::string text = kind_it->second;
    for (const auto& [key, value] : vars) {
        const std::string needle = "{" + key + "}";
        for (std::size_t pos = text.find(needle); pos != std::string::npos;
             pos = text.find(needle, pos + value.size())) {
            text.replace(pos, needle.size(), value);
        }
    }
    return text;
}

} // namespace zf
