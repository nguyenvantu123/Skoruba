import 'dart:convert';

import 'package:http/http.dart' as http;

class PublicTenantApiDto {
  PublicTenantApiDto({required this.displayName});

  final String displayName;

  factory PublicTenantApiDto.fromJson(Map<String, dynamic> json) {
    return PublicTenantApiDto(
      displayName: json['displayName'] as String? ?? '',
    );
  }
}

class TenantPublicDirectoryClient {
  TenantPublicDirectoryClient({
    required this.baseUrl,
    http.Client? httpClient,
  }) : _httpClient = httpClient ?? http.Client();

  final String baseUrl;
  final http.Client _httpClient;

  Future<List<PublicTenantApiDto>> searchTenants(String search) async {
    final normalized = search.trim();

    if (normalized.length < 2) {
      return const [];
    }

    final uri = Uri.parse('$baseUrl/api/tenants/public')
        .replace(queryParameters: {'search': normalized});

    final response = await _httpClient.get(
      uri,
      headers: const {
        'Accept': 'application/json',
      },
    );

    if (response.statusCode == 200) {
      final payload = jsonDecode(response.body) as List<dynamic>;
      return payload
          .cast<Map<String, dynamic>>()
          .map(PublicTenantApiDto.fromJson)
          .toList();
    }

    if (response.statusCode == 400) {
      throw FormatException('Invalid tenant search request: ${response.body}');
    }

    if (response.statusCode == 429) {
      throw Exception('Tenant search rate limit exceeded.');
    }

    throw Exception(
      'Tenant search failed with status ${response.statusCode}: ${response.body}',
    );
  }
}

// Example usage:
// final client = TenantPublicDirectoryClient(baseUrl: 'https://localhost:7397');
// final tenants = await client.searchTenants('branch');
