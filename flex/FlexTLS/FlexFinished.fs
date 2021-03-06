(*
 * Copyright 2015 INRIA and Microsoft Corporation
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)

#light "off"

module FlexTLS.FlexFinished

open NLog

open Bytes
open Error
open TLSInfo
open TLSConstants
open HandshakeMessages

open FlexTypes
open FlexConstants
open FlexState
open FlexHandshake
open FlexSecrets

/// <summary>
/// Module receiving, sending and forwarding TLS Finished messages.
/// </summary>
type FlexFinished =
    class

    /// <summary>
    /// Receive a Finished message from the network stream and check the verify_data on demand
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="nsc"> NextSecurityContext embedding required parameters </param>
    /// <param name="role"> Optional role used to compute an eventual verify data </param>
    /// <returns> Updated state * FFinished message record </returns>
    static member receive (st:state, nsc:nextSecurityContext, ?role:Role) : state * FFinished =
        match role with
        | Some(role) ->
            FlexFinished.receive (st,nsc.si.protocol_version,nsc.si.cipher_suite,verify_data=(FlexSecrets.makeVerifyData nsc.si nsc.secrets.ms role st.hs_log))
        | None ->
            FlexFinished.receive (st,nsc.si.protocol_version,nsc.si.cipher_suite)

    /// <summary>
    /// Receive a Finished message from the network stream and check the verify_data on demand
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="pv"> Protocol Version </param>
    /// <param name="cs"> Ciphersuite </param>
    /// <param name="verify_data"> Optional verify_data to compare to received payload </param>
    /// <returns> Updated state * FFinished message record </returns>
    static member receive (st:state, pv:ProtocolVersion, cs:cipherSuite, ?verify_data:bytes) : state * FFinished =
        LogManager.GetLogger("file").Info("# FINISHED : FlexFinished.receive");

        let st,hstype,payload,to_log = FlexHandshake.receive(st) in
        match hstype with
        | HT_finished  ->
            // check that the received payload has a correct length
            let expected_vd_length =
                match pv with
                | TLS_1p2   -> (TLSConstants.verifyDataLen_of_ciphersuite cs) = (Bytes.length payload)
                | _         -> 12 = (Bytes.length payload)
            in
            if not expected_vd_length then failwith (perror __SOURCE_FILE__ __LINE__ (sprintf "unexpected payload length %d" (Bytes.length payload)))
            else
                // check the verify_data if the user provided one
                (match verify_data with
                | None ->
                    LogManager.GetLogger("file").Debug(sprintf "--- Verify data not checked")
                | Some(verify_data) ->
                    LogManager.GetLogger("file").Debug(sprintf "--- Expected data : %A" (Bytes.hexString(verify_data)));
                    LogManager.GetLogger("file").Debug(sprintf "--- Verify data: %A" (Bytes.hexString(payload)));
                    if not (verify_data = payload) then failwith "Verify data do not match"
                );
                // no verify_data provided OR expected verify_data matches payload
                let st = FlexState.updateIncomingVerifyData st payload in
                let ff = {  verify_data = payload;
                            payload = to_log;
                } in
                LogManager.GetLogger("file").Debug(sprintf "--- Payload : %A" (Bytes.hexString(ff.payload)));
                st,ff
        | _ -> failwith (perror __SOURCE_FILE__ __LINE__ (sprintf "Unexpected handshake type: %A" hstype))

    /// <summary>
    /// Prepare a Finished message from the verify_data that will not be sent to the network
    /// </summary>
    /// <param name="verify_data"> Verify_data that will be used to generate the finished message </param>
    /// <returns> Finished message bytes *  FFinished message record </returns>
    static member prepare (verify_data:bytes) : bytes * FFinished =
        let payload = HandshakeMessages.messageBytes HT_finished verify_data in
        let ff = { verify_data = verify_data;
                   payload = payload;
                 }
        in
        payload,ff

    /// <summary>
    /// Overload : Send a Finished message from the network stream and check the verify_data on demand
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="ff"> Optional finished message record including the payload to be used </param>
    /// <param name="fp"> Optional fragmentation policy at the record level </param>
    /// <returns> Updated state * FFinished message record </returns>
    static member send (st:state, ff:FFinished, ?fp:fragmentationPolicy) : state * FFinished =
        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in
        FlexFinished.send(st,ff.verify_data,fp=fp)

    /// <summary>
    /// Send a Finished message from the verify_data and send it to the network stream
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="role"> Role necessary to compute the verify data </param>
    /// <param name="fp"> Optional fragmentation policy at the record level </param>
    /// <returns> Updated state * FFinished message record </returns>
    static member send (st:state, nsc:nextSecurityContext, role:Role, ?fp:fragmentationPolicy) : state * FFinished =
        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in
        let verify_data =FlexSecrets.makeVerifyData nsc.si nsc.secrets.ms role st.hs_log in
        FlexFinished.send (st,verify_data,fp)

    /// <summary>
    /// Send a Finished message from the network stream and check the verify_data on demand
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="verify_data"> Verify_data that will be used </param>
    /// <param name="fp"> Optional fragmentation policy at the record level </param>
    /// <returns> Updated state * FFinished message record </returns>
    static member send (st:state, verify_data:bytes, ?fp:fragmentationPolicy) : state * FFinished =
        LogManager.GetLogger("file").Info("# FINISHED : FlexFinished.send");

        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in

        let payload,ff = FlexFinished.prepare verify_data in
        LogManager.GetLogger("file").Debug(sprintf "--- Verify data : %A" (Bytes.hexString(ff.verify_data)));

        let st = FlexState.updateOutgoingVerifyData st verify_data in
        let st = FlexHandshake.send(st,payload,fp) in
        st,ff

    end
