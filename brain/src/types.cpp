#include "types.hpp"

namespace zf {

const char* to_key(FactionState state) {
    switch (state) {
    case FactionState::Spokoj:
        return "spokoj";
    case FactionState::Napiecie:
        return "napiecie";
    case FactionState::Wojna:
        return "wojna";
    }
    return "spokoj";
}

FactionState parse_faction_state(std::string_view key) {
    for (const FactionState state : kFactionStates) {
        if (key == to_key(state)) {
            return state;
        }
    }
    return FactionState::Spokoj;
}

const char* display(FactionState state) {
    // "spokój" ma ogonek, ale raport od zawsze pokazywał klucz — zostaje jak było.
    return state == FactionState::Napiecie ? "napięcie" : to_key(state);
}

const char* to_key(ContractKind kind) {
    switch (kind) {
    case ContractKind::Dostawa:
        return "dostawa";
    case ContractKind::Nagroda:
        return "nagroda";
    case ContractKind::Transport:
        return "transport";
    case ContractKind::Naprawa:
        return "naprawa";
    case ContractKind::Poszukiwania:
        return "poszukiwania";
    case ContractKind::Wlasne:
        return "wlasne";
    }
    return "dostawa";
}

std::optional<ContractKind> parse_contract_kind(std::string_view key) {
    for (const ContractKind kind : kContractKinds) {
        if (key == to_key(kind)) {
            return kind;
        }
    }
    return std::nullopt;
}

} // namespace zf
